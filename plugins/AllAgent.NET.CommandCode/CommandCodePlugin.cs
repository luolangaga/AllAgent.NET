
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.CommandCode;

public sealed class CommandCodePlugin : IAllAgentPlugin
{
    private CommandCodeAccount? account;
    public string Id => "allagent.commandcode";
    public string Name => "CommandCode";
    public void Register(PluginContext context)
    {
        account = new(context.DataDirectory);
        context.AddAuthentication(account);
        context.AddProvider(account.ModelProvider);
        context.AddModelFooter(account.FooterProvider);
        context.AddCommand(new("commandcode-status", "查看 CommandCode 登录状态与额度", async (_, ct) =>
            await account.GetStatusWithQuotaAsync(ct)));
    }
    public ValueTask DisposeAsync() { account?.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class CommandCodeAccount : IAuthenticationProvider, IAccountModelProvider, IInteractiveAuthentication, IDisposable
{
    public const string Endpoint = "https://api.commandcode.ai";
    private readonly TokenVault vault;


    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };



    public string Id => "commandcode";
    public string Name => "CommandCode";
    public string LoginInstructions => "在 /login commandcode 中直接粘贴以 user_ 开头的 API Key；或在启动前设置 COMMANDCODE_API_KEY 环境变量。密钥通过 TokenVault（DPAPI）加密保存。";
    public string InputPrompt => "粘贴 CommandCode API Key（user_ 开头，官网账号页复制）";
    public IAiProvider ModelProvider { get; }
    public CommandCodeAccount(string directory, HttpClient? client = null)
    {

        vault = new(Path.Combine(directory, "credentials"));
        if (client != null) http = client;
        ModelProvider = new CommandCodeProvider(this, http);
    }
    public ApiConfig CreateConfiguration() => new() { ProviderId = Id, Protocol = Id, AuthenticationProviderId = Id, DisplayName = Name, BaseUrl = Endpoint, ReasoningProtocol = "openai" };
    public void ValidateModelConfiguration(ApiConfig config)
    {
        if (config.Protocol != Id || config.BaseUrl.TrimEnd('/') != Endpoint)
            throw new InvalidOperationException("CommandCode 凭据只能绑定 CommandCode 插件协议及官方地址。");
    }
    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("COMMANDCODE_API_KEY");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(LoginInstructions);
        await LoginWithKeyAsync(token, ct);
    }
    public Task LoginWithInputAsync(string input, CancellationToken ct) => LoginWithKeyAsync(input, ct);
    // The official CLI accepts a pasted key with surrounding text ("Bearer user_xxx", "token_user_xxx") and cleans it.
    private static readonly System.Text.RegularExpressions.Regex KeyPattern = new("user_[A-Za-z0-9_-]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private async Task LoginWithKeyAsync(string input, CancellationToken ct)
    {
        var token = KeyPattern.Match(input?.Trim() ?? "").Value;
        if (token.Length == 0) throw new InvalidOperationException("密钥格式无效：需要包含 user_ 开头的 API Key（可整段粘贴 'Bearer user_xxx'，不接受 sk- 前缀）。");
        // Verify before replacing an existing credential; never save a rejected key.
        await FetchModelsAsync(token, ct);
        await vault.SaveAsync(Id, new(token, null, DateTimeOffset.MaxValue), ct);
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct) => (await vault.ReadAsync(Id, ct))?.AccessToken;
    public async Task LogoutAsync(CancellationToken ct) => await vault.DeleteAsync(Id, ct);
    /// <summary>Model footer: remaining credits and plan, matching the official /usage view.</summary>
    public ModelFooterProvider FooterProvider => new("CommandCode 额度",
        context => context.AuthenticationProviderId == Id || context.ProviderId == Id,
        (_, ct) => FetchQuotaAsync(ct));
    public readonly record struct Quota(double Monthly, double Purchased, double Free)
    {
        public double Total => Monthly + Purchased + Free;
        public string Describe()
        {
            var parts = new List<string>();
            if (Monthly > 0) parts.Add("月付 " + Format(Monthly));
            if (Purchased > 0) parts.Add("购买 " + Format(Purchased));
            if (Free > 0) parts.Add("免费 " + Format(Free));
            return "剩余 " + Format(Total) + (parts.Count > 0 ? "（" + string.Join(" · ", parts) + "）" : "");
        }
        private static string Format(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
    /// <summary>Official credits body: { credits: { monthlyCredits, purchasedCredits, freeCredits } } — remaining amounts.</summary>
    public static bool TryReadCredits(JsonElement payload, out Quota quota)
    {
        quota = default;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        double Read(string name)
        {
            if (!credits.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return 0;
            found = true; return Math.Max(0, number);
        }
        var monthly = Read("monthlyCredits"); var purchased = Read("purchasedCredits"); var free = Read("freeCredits");
        if (!found) return false;
        quota = new(monthly, purchased, free); return true;
    }
    public async Task<IReadOnlyList<ModelFooterItem>> FetchQuotaAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        if (token == null) return [new("CommandCode 额度", "未登录")];
        try
        {
            var orgId = await FetchOrgIdAsync(token, ct);
            using var credits = await GetAsync("/alpha/billing/credits" + OrgQuery(orgId), token, ct);
            if (!credits.IsSuccessStatusCode)
                return [new("CommandCode 额度", credits.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "当前授权无法读取" : "查询失败 HTTP " + (int)credits.StatusCode)];
            using var creditsJson = JsonDocument.Parse(await credits.Content.ReadAsStringAsync(ct));
            if (!TryReadCredits(creditsJson.RootElement, out var quota)) return [new("CommandCode 额度", "接口未返回额度字段")];
            var items = new List<ModelFooterItem> { new("CommandCode 额度", quota.Describe()) };
            if (await FetchPlanAsync(token, orgId, ct) is { Length: > 0 } plan) items.Add(new("CommandCode 计划", plan));
            return items;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [new("CommandCode 额度", "查询失败：" + ex.Message)];
        }
    }
    internal async Task<string> GetStatusWithQuotaAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (!status.LoggedIn) return status.Message ?? LoginInstructions;
        var quota = await FetchQuotaAsync(ct);
        return string.Join('\n', new[] { status.Message ?? "" }.Concat(quota.Select(item => item.Label + "：" + item.Value)));
    }
    private static string OrgQuery(string? orgId) => orgId == null ? "" : "?orgId=" + Uri.EscapeDataString(orgId);
    private async Task<string?> FetchOrgIdAsync(string token, CancellationToken ct)
    {
        using var response = await GetAsync("/alpha/whoami?limits=1", token, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }
    private async Task<string?> FetchPlanAsync(string token, string? orgId, CancellationToken ct)
    {
        using var response = await GetAsync("/alpha/billing/subscriptions" + OrgQuery(orgId), token, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("planId", out var planId) || planId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(planId.GetString())) return null;
        var label = planId.GetString()!;
        if (data.TryGetProperty("currentPeriodEnd", out var end) && end.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(end.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var ends))
            label += " · " + Math.Max(0, (int)Math.Ceiling((ends - DateTimeOffset.UtcNow).TotalDays)) + " 天后重置";
        return label;
    }
    private async Task<HttpResponseMessage> GetAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-cli-environment", "production");
        request.Headers.Add("x-command-code-version", "1.53.1");
        request.Headers.Add("User-Agent", "cli");
        return await http.SendAsync(request, ct);
    }
    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        var loggedIn = await GetAccessTokenAsync(ct) != null;
        return new(loggedIn, loggedIn, Message: loggedIn ? "CommandCode 已登录；/logout commandcode 可清除凭据。" : LoginInstructions);
    }
    internal async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(string token, CancellationToken ct)
    {
        // Official CLI catalog view: /provider/v1/models?view=client
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/provider/v1/models?view=client");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-cli-environment", "production");
        request.Headers.Add("x-command-code-version", "1.53.1");
        request.Headers.Add("User-Agent", "cli");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("CommandCode 模型查询失败：HTTP " + (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("data").EnumerateArray().Select(model =>
        {
            if (!model.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idNode.GetString())) return null;
            var id = idNode.GetString()!;
            var name = model.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object
                && display.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString())
                ? label.GetString()!
                : model.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nameNode.GetString()) ? nameNode.GetString()! : id;
            // An explicit null/empty list means the model decides its own reasoning depth; only an
            // absent field leaves the host's family fallback in place.
            IReadOnlyList<string>? efforts = null;
            if (model.TryGetProperty("reasoningEfforts", out var levels))
                efforts = levels.ValueKind == JsonValueKind.Array
                    ? levels.EnumerateArray().Select(level => level.ValueKind == JsonValueKind.String ? level.GetString() : null)
                        .Where(level => !string.IsNullOrWhiteSpace(level)).Select(level => level == "off" ? "none" : level!).Distinct().ToArray()
                    : [];
            int? ReadLimit(string key) => model.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number) && number > 0 ? number : null;
            return new ModelInfo(id, name, ReadLimit("context_length"), ReadLimit("maxOutputTokens")) { ReasoningLevels = efforts };
        }).Where(model => model != null).Select(model => model!).DistinctBy(model => model.Id).ToArray();
    }
    public void Dispose() => http.Dispose();
}
