using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.WorkBuddy;

public sealed class WorkBuddyPlugin : ILanAgentPlugin
{
    public string Id => "lanagent.workbuddy";
    public string Name => "WorkBuddy";
    private readonly List<WorkBuddyAccount> accounts = [];
    public void Register(PluginContext context)
    {
        foreach (var international in new[] { false, true })
        {
            var account = new WorkBuddyAccount(context.DataDirectory, international);
            accounts.Add(account);
            context.AddModelFooter(new(account.Id,
                model => model.AuthenticationProviderId == account.Id || model.ProviderId == account.Id,
                async (_, ct) => new ModelFooterItem[] { new("", await account.QuotaAsync(ct)) }));
            context.AddAuthentication(account);
            context.AddProvider(account.ModelProvider);
            context.AddCommand(new(account.Id + "-quota", "查询 WorkBuddy 账号额度", async (_, ct) => await account.QuotaAsync(ct)));
            context.AddCommand(new(account.Id + "-status", "查询 WorkBuddy 账号状态", async (_, ct) =>
            {
                var status = await account.GetStatusAsync(ct);
                return account.Name + " · " + (status.LoggedIn ? "已登录" : "未登录") +
                    (status.Account == null ? "" : " · " + status.Account) + " · " + status.Message;
            }));
        }
    }
    public ValueTask DisposeAsync() { foreach (var account in accounts) account.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class WorkBuddyAccount : IAuthenticationProvider, IAccountModelProvider, IDisposable
{
    private readonly TokenVault vault;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly bool international;
    public string Id => international ? "workbuddy-intl" : "workbuddy";
    public string Name => international ? "WorkBuddy 国际版" : "WorkBuddy 国内版";
    public string Endpoint => international ? "https://www.workbuddy.ai" : "https://copilot.tencent.com";
    private string Origin => international ? Endpoint : "https://www.codebuddy.cn";
    public string? LoginInstructions => "在浏览器完成 WorkBuddy 授权；终端自动等待结果，Esc 可取消。";
    public IAiProvider ModelProvider { get; }
    public WorkBuddyAccount(string directory, bool international = false, HttpClient? http = null)
    {
        this.international = international;
        vault = new TokenVault(Path.Combine(directory, "credentials"));
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        ModelProvider = new WorkBuddyProvider(this);
    }
    public ApiConfig CreateConfiguration() => new() { ProviderId = Id, Protocol = Id, BaseUrl = Endpoint + "/v2", DisplayName = Name, AuthenticationProviderId = Id, ReasoningProtocol = "none" };
    public void ValidateModelConfiguration(ApiConfig config)
    {
        if (config.BaseUrl.TrimEnd('/') != Endpoint + "/v2" || config.Protocol != Id)
            throw new InvalidOperationException("WorkBuddy 认证只能绑定对应区域的 WorkBuddy 插件协议。");
    }
    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await gate.WaitAsync(deadline.Token);
        try
        {
            var start = await JsonAsync("/v2/plugin/auth/state?platform=WorkBuddy", HttpMethod.Post, null, new { }, deadline.Token);
            var data = Data(start);
            var state = data.GetProperty("state").GetString() ?? throw new InvalidOperationException("授权缺少 state。");
            var uri = new Uri(data.GetProperty("authUrl").GetString()!);
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidOperationException("授权地址无效。");
            await openBrowser(uri, deadline.Token);
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                var result = await JsonAsync("/v2/plugin/auth/token?state=" + Uri.EscapeDataString(state), HttpMethod.Get, null, null, deadline.Token, true);
                if (result.TryGetProperty("code", out var code) && code.GetInt32() == 11217) continue;
                Check(result);
                var tokenData = Data(result);
                if (!tokenData.TryGetProperty("accessToken", out var token) || string.IsNullOrWhiteSpace(token.GetString())) continue;
                await SaveAsync(tokenData, null, deadline.Token);
                return;
            }
        }
        finally { gate.Release(); }
    }
    private async Task SaveAsync(JsonElement data, string? previousRefresh, CancellationToken ct)
    {
        var token = data.GetProperty("accessToken").GetString()!;
        var claims = Claims(token);
        var expiry = claims.TryGetProperty("exp", out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) : DateTimeOffset.UtcNow.AddMinutes(30);
        var refresh = data.TryGetProperty("refreshToken", out var r) ? r.GetString() : previousRefresh;
        await vault.SaveAsync(Id, new(token, refresh, expiry), ct);
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var saved = await vault.ReadAsync(Id, ct);
            if (saved == null) return null;
            if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return saved.AccessToken;
            if (string.IsNullOrEmpty(saved.RefreshToken)) throw new InvalidOperationException("登录已过期；请 /login " + Id);
            using var request = Request("/v2/plugin/auth/token/refresh", HttpMethod.Post, saved.AccessToken, new { });
            request.Headers.TryAddWithoutValidation("X-Refresh-Token", saved.RefreshToken);
            request.Headers.TryAddWithoutValidation("X-Auth-Refresh-Source", international ? "plugin" : "workbuddy");
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            Check(result);
            var data = Data(result);
            if (data.TryGetProperty("data", out var inner)) data = inner;
            await SaveAsync(data, saved.RefreshToken, ct);
            return data.GetProperty("accessToken").GetString();
        }
        finally { gate.Release(); }
    }
    public async Task LogoutAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await vault.DeleteAsync(Id, ct); } finally { gate.Release(); }
    }
    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        return new(token != null, token != null, token == null ? null : Claims(token).GetProperty("sub").GetString(),
            token == null ? "使用 /login " + Id : "查看额度：/ext " + Id + "-quota");
    }
    internal Dictionary<string, string> Headers(string token)
    {
        var claims = Claims(token);
        var uid = claims.GetProperty("sub").GetString()!;
        var version = international ? "5.5.2" : "5.5.6";
        var headers = new Dictionary<string, string>
        {
            ["X-User-Id"] = uid, ["X-Domain"] = new Uri(Endpoint).Host,
            ["X-IDE-Type"] = "WorkBuddy", ["X-IDE-Name"] = "WorkBuddy", ["X-IDE-Version"] = version,
            ["X-Product"] = "WorkBuddy", ["X-Agent-Purpose"] = "conversation",
            ["User-Agent"] = international ? "WorkBuddy/5.5.2 WorkBuddy AI/5.5.2 CLI/5.5.2" : "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1",
            ["Origin"] = Origin, ["Referer"] = Origin + "/", ["X-CodeBuddy-Request"] = "1",
            ["X-Requested-With"] = "XMLHttpRequest", ["X-Request-ID"] = Guid.NewGuid().ToString("N"),
            ["X-Machine-ID"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid + ":machine"))),
            ["X-Session-ID"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid + ":session")))
        };
        if (claims.TryGetProperty("enterprise_id", out var enterprise) && !string.IsNullOrEmpty(enterprise.GetString()))
        { headers["X-Enterprise-Id"] = enterprise.GetString()!; headers["X-Tenant-Id"] = enterprise.GetString()!; }
        else headers["X-No-Enterprise-Id"] = "1";
        return headers;
    }
    private HttpRequestMessage Request(string path, HttpMethod method, string? token, object? body)
    {
        var request = new HttpRequestMessage(method, Endpoint + path);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var headers = token == null ? new Dictionary<string, string> { ["Origin"] = Origin, ["Referer"] = Origin + "/", ["User-Agent"] = "WorkBuddy/5.5.6", ["X-Requested-With"] = "XMLHttpRequest" } : Headers(token);
        foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        return request;
    }
    internal async Task<JsonElement> JsonAsync(string path, HttpMethod method, string? token, object? body, CancellationToken ct, bool pending = false)
    {
        using var request = Request(path, method, token, body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        if (!pending) Check(result);
        return result;
    }
    private static void Check(JsonElement result)
    {
        if (result.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            throw new InvalidOperationException("WorkBuddy 上游错误，code=" + code.GetInt32());
    }
    internal static JsonElement Data(JsonElement result) => result.TryGetProperty("data", out var data) ? data : throw new InvalidOperationException("WorkBuddy 响应缺少 data。");
    private static JsonElement Claims(string token)
    {
        var segment = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '=')));
    }
    public async Task<string> QuotaAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login " + Id);
        // Billing uses a separate origin in the mainland region.
        using var request = Request("/v2/billing/meter/get-user-resource", HttpMethod.Post, token, new { PageNumber = 1, PageSize = 100, ProductCode = "p_tcaca", Status = new[] { 0, 3 }, PackageEndTimeRangeBegin = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"), PackageEndTimeRangeEnd = "2036-01-01 00:00:00" });
        request.RequestUri = new Uri(Origin + "/v2/billing/meter/get-user-resource");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        Check(result);
        var data = Data(result);
        if (!data.TryGetProperty("Response", out var envelope) || !envelope.TryGetProperty("Data", out var resources) || !resources.TryGetProperty("Accounts", out var packages))
            throw new InvalidOperationException("额度响应格式变化或账号无权限，无法读取额度。");
        double remaining = 0, used = 0;
        foreach (var package in packages.EnumerateArray())
        {
            remaining += Capacity(package, "CycleCapacityRemainPrecise", "CycleCapacityRemain", "CapacityRemainPrecise", "CapacityRemain");
            used += Capacity(package, "CycleCapacityUsedPrecise", "CycleCapacityUsed", "CapacityUsedPrecise", "CapacityUsed");
        }
        return Name + " · 剩余额度：" + remaining.ToString("0.##") + " · 已使用：" + used.ToString("0.##") + " · 套餐数：" + packages.GetArrayLength();
    }
    private static double Capacity(JsonElement package, params string[] keys)
    {
        foreach (var key in keys)
            if (package.TryGetProperty(key, out var value) && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)) return number;
        throw new InvalidOperationException("套餐额度字段缺失，无法确认额度。");
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}

public sealed class WorkBuddyProvider(WorkBuddyAccount account) : IAiProvider
{
    public string ProviderId => account.Id;
    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        account.ValidateModelConfiguration(request.Config);
        var token = await account.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login " + account.Id);
        var config = JsonSerializer.Deserialize<ApiConfig>(JsonSerializer.Serialize(request.Config))!;
        config.ApiKey = token; config.ApiKeyEnvironmentVariable = null;
        config.ExtraHeadersJson = JsonSerializer.Serialize(account.Headers(token));
        await foreach (var chunk in new OpenAiProvider().StreamChatAsync(request with { Config = config }, ct)) yield return chunk;
    }
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        account.ValidateModelConfiguration(config);
        var token = await account.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login " + account.Id);
        JsonElement data;
        try { data = WorkBuddyAccount.Data(await account.JsonAsync("/v3/config", HttpMethod.Get, token, null, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        { data = WorkBuddyAccount.Data(await account.JsonAsync("/v2/enterprises/personal/models", HttpMethod.Get, token, null, ct)); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (data.TryGetProperty("agents", out var agents))
            foreach (var agent in agents.EnumerateArray())
                if (agent.TryGetProperty("models", out var models))
                    foreach (var model in models.EnumerateArray())
                        if (model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString())) ids.Add(model.GetString()!);
        return ids.Select(id => new ModelInfo(id, id)).ToArray();
    }
}
