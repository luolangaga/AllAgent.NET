using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.WorkBuddy;

public sealed class WorkBuddyPlugin : IAllAgentPlugin
{
    public string Id => "allagent.workbuddy";
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
            context.AddCommand(new(account.Id + "-automation", "立即执行 WorkBuddy 自动巡检", (_, ct) => account.Automation.RunCycleAsync("manual", ct)));
            context.AddCommand(new(account.Id + "-history", "查看 WorkBuddy 自动化活动历史", (args, ct) => account.Automation.HistoryAsync(HistoryLimit(args), ct)));
            context.AddCommand(new(account.Id + "-scheduler", "查看 WorkBuddy 后台自动化状态", (_, ct) => Task.FromResult(account.Automation.Status())));
            if (!international)
            {
                context.AddCommand(new(account.Id + "-checkin", "国内版每日签到与对话活跃上报", (_, ct) => account.Automation.CheckinAndReportAsync("manual", ct)));
                context.AddCommand(new(account.Id + "-growth", "国内版成长任务、连登与猫猫旅行", (_, ct) => account.Automation.RunGrowthAsync("manual", ct)));
            }
            account.Automation.Start();
        }
    }
    private static int HistoryLimit(string[] args) => args.Length > 0 && int.TryParse(args[0], out var value) ? Math.Clamp(value, 1, 1000) : 20;
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
    internal bool International => international;
    internal string ChatBase => Endpoint;
    internal string BillingBase => Origin;
    internal string WebBase => international ? "https://www.workbuddy.ai" : "https://www.workbuddy.cn";
    internal string DataDirectory { get; }
    internal HttpClient Http => http;
    public string? LoginInstructions => "在浏览器完成 WorkBuddy 授权；终端自动等待结果，Esc 可取消。";
    public IAiProvider ModelProvider { get; }
    public WorkBuddyAutomation Automation { get; }
    public WorkBuddyAccount(string directory, bool international = false, HttpClient? http = null)
    {
        this.international = international;
        DataDirectory = directory;
        vault = new TokenVault(Path.Combine(directory, "credentials"));
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        ModelProvider = new WorkBuddyProvider(this);
        Automation = new WorkBuddyAutomation(this, directory);
    }
    public ApiConfig CreateConfiguration() => new() { ProviderId = Id, Protocol = Id, BaseUrl = Endpoint + "/v2", DisplayName = Name, AuthenticationProviderId = Id, ReasoningProtocol = "workbuddy", ContextWindowTokens = 300000 };
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
            return await RefreshLockedAsync(saved, ct);
        }
        finally { gate.Release(); }
    }
    private async Task<string> RefreshLockedAsync(OAuthTokens saved, CancellationToken ct)
    {
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
        return data.GetProperty("accessToken").GetString()!;
    }
    internal async Task<bool> KeepAliveAsync(TimeSpan minimumRemaining, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var saved = await vault.ReadAsync(Id, ct);
            if (saved == null) return false;
            if (saved.ExpiresAt - DateTimeOffset.UtcNow >= minimumRemaining) return false;
            if (string.IsNullOrEmpty(saved.RefreshToken)) throw new InvalidOperationException("登录已过期；请 /login " + Id);
            await RefreshLockedAsync(saved, ct);
            return true;
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
    internal Dictionary<string, string> BillingHeaders(string token)
    {
        var claims = Claims(token);
        var uid = claims.GetProperty("sub").GetString()!;
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json, text/plain, */*",
            ["X-Requested-With"] = "XMLHttpRequest",
            ["User-Agent"] = international ? "WorkBuddy/5.5.2" : "WorkBuddy/5.5.6",
            ["Origin"] = Origin, ["Referer"] = Origin + "/",
            ["X-User-Id"] = uid, ["X-Domain"] = new Uri(Endpoint).Host,
            ["X-CodeBuddy-Request"] = "1",
            ["Accept-Language"] = international ? "en-US" : "zh-CN",
            ["X-Request-ID"] = Guid.NewGuid().ToString("N"),
            ["X-Machine-ID"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid + ":machine"))),
            ["X-Session-ID"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid + ":session")))
        };
        if (!international) headers["X-Product"] = "SaaS";
        if (claims.TryGetProperty("enterprise_id", out var enterprise) && !string.IsNullOrEmpty(enterprise.GetString()))
        { headers["X-Enterprise-Id"] = enterprise.GetString()!; headers["X-Tenant-Id"] = enterprise.GetString()!; }
        else headers["X-No-Enterprise-Id"] = "1";
        return headers;
    }
    private HttpRequestMessage Request(string path, HttpMethod method, string? token, object? body) => Request(new Uri(Endpoint + path), method, token, body, false);
    internal HttpRequestMessage Request(Uri uri, HttpMethod method, string? token, object? body, bool billing = false)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var headers = token == null ? new Dictionary<string, string> { ["Origin"] = Origin, ["Referer"] = Origin + "/", ["User-Agent"] = "WorkBuddy/5.5.6", ["X-Requested-With"] = "XMLHttpRequest" } : billing ? BillingHeaders(token) : Headers(token);
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
    internal static JsonElement Claims(string token)
    {
        var segment = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '=')));
    }
    internal static string Uid(string token)
    {
        try { return Claims(token).TryGetProperty("sub", out var sub) ? sub.GetString() ?? "" : ""; }
        catch { return ""; }
    }
    internal static bool IsEnterprise(string token)
    {
        try { return Claims(token).TryGetProperty("enterprise_id", out var id) && !string.IsNullOrEmpty(id.GetString()); }
        catch { return false; }
    }
    public async Task<string> QuotaAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login " + Id);
        // Billing uses a separate origin in the mainland region.
        using var request = Request(new Uri(Origin + "/v2/billing/meter/get-user-resource"), HttpMethod.Post, token, new { PageNumber = 1, PageSize = 100, ProductCode = "p_tcaca", Status = new[] { 0, 3 }, PackageEndTimeRangeBegin = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"), PackageEndTimeRangeEnd = "2036-01-01 00:00:00" }, true);
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
    public void Dispose() { Automation.Dispose(); http.Dispose(); gate.Dispose(); }
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
        var found = new Dictionary<string, ModelInfo>(StringComparer.Ordinal);
        if (data.TryGetProperty("agents", out var agents))
            foreach (var agent in agents.EnumerateArray())
                if (agent.TryGetProperty("models", out var models))
                    foreach (var model in models.EnumerateArray())
                        if (ReadModel(model) is { } info) found[info.Id] = info;
        return found.Values.ToArray();
    }
    /// <summary>Models arrive as bare IDs on some accounts and as objects with declared limits and
    /// reasoning levels on others; keep whichever spec the endpoint advertises.</summary>
    private static ModelInfo? ReadModel(JsonElement model)
    {
        if (model.ValueKind == JsonValueKind.String)
        {
            var text = model.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : new ModelInfo(text, text);
        }
        if (model.ValueKind != JsonValueKind.Object) return null;
        var id = ReadText(model, "id") ?? ReadText(model, "modelId") ?? ReadText(model, "model_id") ?? ReadText(model, "name");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = ReadText(model, "displayName") ?? ReadText(model, "display_name") ?? ReadText(model, "name") ?? id;
        return new ModelInfo(id, name,
            ReadLimit(model, "contextLength", "context_length", "contextWindow", "context_window", "maxContextTokens", "max_context_tokens"),
            ReadLimit(model, "maxOutputTokens", "max_output_tokens", "maxTokens", "max_tokens"))
        {
            ReasoningLevels = ReadLevels(model)
        };
    }
    private static string? ReadText(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static int? ReadLimit(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
            if (item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0) return number;
        return null;
    }
    private static IReadOnlyList<string>? ReadLevels(JsonElement item)
    {
        foreach (var property in new[] { "reasoningEfforts", "reasoning_efforts", "reasoningEffortLevels", "supportedEfforts", "supported_efforts", "efforts" })
        {
            if (!item.TryGetProperty(property, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
                return Levels(value.EnumerateArray().Select(level => level.ValueKind == JsonValueKind.String ? level.GetString() : null));
            if (value.ValueKind == JsonValueKind.Object) return Levels(value.EnumerateObject().Select(level => level.Name));
            // An explicit null/empty list means the model decides its own depth.
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.False) return [];
        }
        return null;
    }
    private static IReadOnlyList<string> Levels(IEnumerable<string?> levels) =>
        levels.Where(level => !string.IsNullOrWhiteSpace(level)).Select(level => level == "off" ? "none" : level!).Distinct(StringComparer.Ordinal).ToArray();
}
