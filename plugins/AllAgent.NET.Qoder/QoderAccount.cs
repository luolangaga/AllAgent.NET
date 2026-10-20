using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Qoder;

/// <summary>Qoder device login, token storage and the account-bound model catalog.</summary>
internal sealed class QoderAccount : IAuthenticationProvider, IAccountModelProvider
{
    private readonly TokenVault vault;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly SemaphoreSlim usageGate = new(1, 1);
    private QoderQuota? quotaCache;
    private DateTimeOffset quotaCacheExpires;
    private IReadOnlyList<QoderActivity>? activityCache;
    private DateTimeOffset activityCacheExpires;

    internal QoderAccount(string directory, HttpClient? http = null)
    {
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        vault = new TokenVault(Path.Combine(directory, "credentials"));
        Models = new QoderModelCatalog(this);
        ModelProvider = new QoderProvider(this);
    }

    public string Id => "qoder";
    public string Name => "Qoder";
    public string? LoginInstructions => "浏览器将打开 Qoder 设备授权页；在网页中确认登录后会自动继续。";
    public IAiProvider ModelProvider { get; }
    internal QoderModelCatalog Models { get; }
    internal HttpClient Http => http;

    public ApiConfig CreateConfiguration() => new()
    {
        ProviderId = Id,
        Protocol = Id,
        BaseUrl = "https://api3.qoder.sh/algo",
        DisplayName = Name,
        AuthenticationProviderId = Id,
        ReasoningProtocol = Id,
        ContextWindowTokens = 200000
    };

    public void ValidateModelConfiguration(ApiConfig config)
    {
        if (!string.Equals(config.ProviderId, Id, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(config.Protocol) && !string.Equals(config.Protocol, Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Qoder 认证只能绑定 Qoder 插件协议。");
    }

    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        await loginGate.WaitAsync(deadline.Token);
        try { await LoginCoreAsync(openBrowser, deadline.Token); }
        finally { loginGate.Release(); }
    }

    private async Task LoginCoreAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        var (verifier, challenge) = QoderProtocol.CreatePkce();
        var nonce = QoderProtocol.NewSimpleUuid();
        var machineId = QoderClientCache.MachineId();
        if (string.IsNullOrWhiteSpace(machineId)) machineId = QoderProtocol.NewMachineId();
        var machine = QoderClientCache.MachineToken();

        var uri = QoderProtocol.BuildVerificationUrl(nonce, challenge, machineId);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("qoder.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Qoder 授权地址无效。");
        await openBrowser(uri, ct);

        while (true)
        {
            using var poll = await PollAsync(nonce, verifier, ct);
            if (poll == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                continue;
            }
            var token = QoderProtocol.Unwrap(poll.RootElement);
            var accessToken = QoderProtocol.ReadString(token, "token", "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                // Some deployments answer 200 with an empty frame while the browser is still authorizing.
                var message = QoderProtocol.ReadString(token, "message", "error_description", "error");
                if (!string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Qoder 授权失败：" + message);
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                continue;
            }

            var refreshToken = QoderProtocol.ReadString(token, "refresh_token");
            var expiresAt = QoderProtocol.ReadExpiry(token, "expires_at", "expires_in", DateTimeOffset.UtcNow.AddDays(30));
            // A skewed/stale upstream timestamp must not make a freshly issued token look expired.
            if (expiresAt < DateTimeOffset.UtcNow.AddDays(1)) expiresAt = DateTimeOffset.UtcNow.AddDays(1);
            using var userInfoDocument = await TryGetJsonAsync(QoderProtocol.UserInfoUrl, accessToken, null, null, ct);
            var userInfo = userInfoDocument == null ? default : QoderProtocol.Unwrap(userInfoDocument.RootElement);
            using var statusDocument = await TryGetJsonAsync(QoderProtocol.UserStatusUrl, accessToken, machine.Token, machine.Type, ct);
            var status = statusDocument == null ? default : QoderProtocol.Unwrap(statusDocument.RootElement);
            CheckWhitelist(status);

            var identity = new QoderIdentity
            {
                UserId = First(
                    QoderProtocol.ReadString(status, "id", "user_id"),
                    QoderProtocol.ReadString(userInfo, "id", "user_id", "uid"),
                    QoderProtocol.ReadString(token, "user_id")) ?? throw new InvalidOperationException("Qoder 授权结果缺少用户 ID，无法建立会话。"),
                Name = First(QoderProtocol.ReadString(status, "name", "displayName"), QoderProtocol.ReadString(userInfo, "name", "username", "user_name")) ?? "",
                Email = First(QoderProtocol.ReadString(status, "email"), QoderProtocol.ReadString(userInfo, "email")) ?? "",
                OrganizationId = First(
                    QoderProtocol.ReadString(status, "orgId", "organizationId"),
                    QoderProtocol.ReadString(userInfo, "organization_id", "organizationId", "orgId")) ?? "",
                OrganizationName = First(
                    QoderProtocol.ReadString(status, "orgName", "organizationName"),
                    QoderProtocol.ReadString(userInfo, "organization_name", "orgName", "organizationName")) ?? "",
                UserType = First(QoderProtocol.ReadString(status, "userType", "user_type"), "personal_standard")!,
                YxUid = First(QoderProtocol.ReadString(status, "yxUid", "yx_uid"), QoderProtocol.ReadString(userInfo, "yxUid", "yx_uid")) ?? "",
                MachineId = machineId,
                MachineToken = string.IsNullOrWhiteSpace(machine.Token) ? machineId : machine.Token,
                MachineType = string.IsNullOrWhiteSpace(machine.Type) ? QoderProtocol.MachineType : machine.Type
            };
            if (string.IsNullOrWhiteSpace(identity.Name)) identity.Name = First(identity.Email, identity.UserId)!;

            await gate.WaitAsync(ct);
            try { await SaveAsync(new QoderSession(accessToken, refreshToken, expiresAt, identity), ct); }
            finally { gate.Release(); }
            quotaCache = null;
            activityCache = null;
            return;
        }
    }

    private async Task<JsonDocument?> PollAsync(string nonce, string verifier, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, QoderProtocol.DevicePollUrl(nonce, verifier));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Accepted) return null;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qoder 设备授权失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
        return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string url, string accessToken, string? machineToken, string? machineType, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Cosy-ClientType", "0");
            request.Headers.TryAddWithoutValidation("Cosy-MachineOS", QoderProtocol.MachineOs);
            if (!string.IsNullOrWhiteSpace(machineToken)) request.Headers.TryAddWithoutValidation("Cosy-MachineToken", machineToken);
            if (!string.IsNullOrWhiteSpace(machineType)) request.Headers.TryAddWithoutValidation("Cosy-MachineType", machineType);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or JsonException or TaskCanceledException))
        {
            return null;
        }
    }

    private static void CheckWhitelist(JsonElement status)
    {
        var state = QoderProtocol.ReadString(status, "whitelistStatus");
        switch (state)
        {
            case "NoIpPermission": throw new InvalidOperationException("企业设置了 IP 白名单，当前网络无法登录 Qoder。");
            case "AppDisable": throw new InvalidOperationException("Qoder 应用已被停用，无法登录。");
            case "LoginExpire": throw new InvalidOperationException("Qoder 登录已失效，请重试。");
            case "NotAllow":
            case "NOT_ALLOW": throw new InvalidOperationException("当前账号暂无 Qoder 使用权限。");
        }
    }

    private async Task<QoderSession?> ReadSessionAsync(CancellationToken ct)
    {
        var tokens = await vault.ReadAsync(Id, ct);
        if (tokens == null) return null;
        QoderIdentity identity;
        try { identity = string.IsNullOrWhiteSpace(tokens.MetadataJson) ? new() : JsonSerializer.Deserialize<QoderIdentity>(tokens.MetadataJson) ?? new(); }
        catch (JsonException) { identity = new(); }
        return new QoderSession(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt, identity);
    }

    internal async Task<QoderSession> GetSessionAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var session = await ReadSessionAsync(ct) ?? throw new InvalidOperationException("请先使用 /login qoder 登录。");
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return session;
            if (string.IsNullOrWhiteSpace(session.RefreshToken))
                throw new InvalidOperationException("Qoder 登录已过期，请重新 /login qoder。");
            try { return await RefreshAsync(session, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException("Qoder 登录已过期且自动续期失败，请重新 /login qoder。", ex);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var session = await ReadSessionAsync(ct);
            if (session == null) return null;
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return session.AccessToken;
            if (string.IsNullOrWhiteSpace(session.RefreshToken)) return null;
            try { return (await RefreshAsync(session, ct)).AccessToken; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }
        finally { gate.Release(); }
    }

    private async Task<QoderSession> RefreshAsync(QoderSession session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, QoderProtocol.RefreshUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { refreshToken = session.RefreshToken }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qoder 令牌续期失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
        using var document = JsonDocument.Parse(body);
        var root = QoderProtocol.Unwrap(document.RootElement);
        var accessToken = QoderProtocol.ReadString(root, "token", "access_token")
            ?? throw new InvalidOperationException("Qoder 令牌续期响应缺少访问令牌。");
        var refreshToken = QoderProtocol.ReadString(root, "refresh_token") ?? session.RefreshToken;
        var expiresAt = QoderProtocol.ReadExpiry(root, "expires_at", "expires_in", DateTimeOffset.UtcNow.AddHours(1));
        if (expiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var identity = session.Identity;
        var userId = QoderProtocol.ReadString(root, "user_id");
        if (!string.IsNullOrWhiteSpace(userId)) identity.UserId = userId;
        var refreshed = new QoderSession(accessToken, refreshToken, expiresAt, identity);
        await SaveAsync(refreshed, ct);
        return refreshed;
    }

    private Task SaveAsync(QoderSession session, CancellationToken ct) =>
        vault.SaveAsync(Id, new OAuthTokens(session.AccessToken, session.RefreshToken, session.ExpiresAt, JsonSerializer.Serialize(session.Identity)), ct);

    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var session = await ReadSessionAsync(ct);
            if (session == null) return new(false, true, Message: "使用 /login qoder 登录；登录后自动连接 Qoder 模型目录。");
            var expired = session.ExpiresAt <= DateTimeOffset.UtcNow && string.IsNullOrWhiteSpace(session.RefreshToken);
            var account = string.IsNullOrWhiteSpace(session.Identity.Email) ? First(session.Identity.Name, session.Identity.UserId) : session.Identity.Email;
            return new(!expired, true, account,
                expired ? "Qoder 登录已过期，请重新 /login qoder。"
                    : "模型目录：/ext qoder-models；额度：/ext qoder-quota；活动：/ext qoder-activities；切换模型：/model");
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await vault.DeleteAsync(Id, ct); }
        finally { gate.Release(); }
        quotaCache = null;
        activityCache = null;
    }

    internal Task<QoderQuota> QuotaAsync(CancellationToken ct) => QuotaAsync(false, ct);

    internal async Task<QoderQuota> QuotaAsync(bool forceRefresh, CancellationToken ct)
    {
        await usageGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && quotaCache != null && DateTimeOffset.UtcNow < quotaCacheExpires) return quotaCache;
            var session = await GetSessionAsync(ct);
            var quota = await FetchQuotaAsync(session, ct);
            quotaCache = quota;
            quotaCacheExpires = DateTimeOffset.UtcNow.AddSeconds(60);
            return quota;
        }
        finally { usageGate.Release(); }
    }

    private async Task<QoderQuota> FetchQuotaAsync(QoderSession session, CancellationToken ct)
    {
        QoderQuota quota;
        try
        {
            using var document = await GetOpenApiAsync(QoderProtocol.QuotaUrl, session, ct);
            EnsureCode(document.RootElement, "额度查询");
            quota = QoderQuota.Parse(document.RootElement);
        }
        catch (QoderAuthException)
        {
            // Device tokens are accepted by the user endpoints but not every
            // quota deployment; the status payload carries the same quota block.
            using var status = await GetOpenApiAsync(QoderProtocol.UserStatusUrl, session, ct);
            EnsureCode(status.RootElement, "额度查询");
            quota = QoderQuota.Parse(status.RootElement);
        }
        try
        {
            using var plan = await GetOpenApiAsync(QoderProtocol.PlanUrl, session, ct);
            quota = QoderQuota.ParsePlan(plan.RootElement, quota);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return quota;
    }

    internal Task<IReadOnlyList<QoderActivity>> ActivitiesAsync(CancellationToken ct) => ActivitiesAsync(false, ct);

    internal async Task<IReadOnlyList<QoderActivity>> ActivitiesAsync(bool forceRefresh, CancellationToken ct)
    {
        await usageGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && activityCache != null && DateTimeOffset.UtcNow < activityCacheExpires) return activityCache;
            var session = await GetSessionAsync(ct);
            using var document = await GetOpenApiAsync(QoderProtocol.ActivityEligibilityUrl, session, ct);
            EnsureCode(document.RootElement, "活动查询");
            var activities = QoderActivity.ParseList(document.RootElement);
            activityCache = activities;
            activityCacheExpires = DateTimeOffset.UtcNow.AddSeconds(120);
            return activities;
        }
        finally { usageGate.Release(); }
    }

    internal async Task<string> ClaimActivityAsync(string activityId, CancellationToken ct)
    {
        activityId = RequireActivityId(activityId);
        await usageGate.WaitAsync(ct);
        try
        {
            var session = await GetSessionAsync(ct);
            var uri = new Uri(QoderProtocol.ActivityClaimUrl + "?activityId=" + Uri.EscapeDataString(activityId));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent([])
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "qoder/1.1.8");
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new QoderAuthException();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Qoder 活动领取失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
            var message = ClaimMessage(body);
            quotaCache = null;
            activityCache = null;
            return message;
        }
        finally { usageGate.Release(); }
    }

    internal async Task<string> CheckInAsync(CancellationToken ct)
    {
        var activities = await ActivitiesAsync(true, ct);
        var claimable = activities.Where(activity => activity.CanClaim).ToArray();
        if (claimable.Length == 0)
            return activities.Count == 0
                ? "当前没有 Qoder 活动。"
                : $"当前没有可领取的 Qoder 活动（共 {activities.Count} 个）。";
        var lines = new List<string>();
        foreach (var activity in claimable)
        {
            try
            {
                var message = await ClaimActivityAsync(activity.Id, ct);
                lines.Add("✓ " + activity.Id + "：" + message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lines.Add("✗ " + activity.Id + "：" + ex.Message);
            }
            if (claimable.Length > 1) await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }
        return "Qoder 活动领取结果：\n" + string.Join("\n", lines);
    }

    private async Task<JsonDocument> GetOpenApiAsync(string url, QoderSession session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "qoder/1.1.8");
        request.Headers.TryAddWithoutValidation("Cosy-ClientType", "0");
        request.Headers.TryAddWithoutValidation("Cosy-MachineOS", QoderProtocol.MachineOs);
        if (!string.IsNullOrWhiteSpace(session.Identity.MachineToken))
            request.Headers.TryAddWithoutValidation("Cosy-MachineToken", session.Identity.MachineToken);
        if (!string.IsNullOrWhiteSpace(session.Identity.MachineType))
            request.Headers.TryAddWithoutValidation("Cosy-MachineType", session.Identity.MachineType);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new QoderAuthException();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qoder 接口请求失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
        return JsonDocument.Parse(body);
    }

    private static void EnsureCode(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var code = QoderProtocol.ReadInt(root, "code");
        if (code is null or 0) return;
        if (code is 401 or 403) throw new QoderAuthException();
        throw new InvalidOperationException("Qoder " + operation + "失败：" + (QoderProtocol.ReadString(root, "msg", "message") ?? "code=" + code));
    }

    private static string ClaimMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "领取成功";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = QoderProtocol.ReadInt(root, "code");
            if (code is 401 or 403) throw new QoderAuthException();
            if (code is not (null or 0))
                throw new InvalidOperationException(QoderProtocol.ReadString(root, "msg", "message") ?? $"Qoder 返回 code={code}");
            if (QoderProtocol.ReadBool(root, "success") == false)
                throw new InvalidOperationException(QoderProtocol.ReadString(root, "msg", "message") ?? "Qoder 未确认领取成功。");
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner : default;
            return QoderProtocol.ReadString(root, "msg", "message")
                ?? QoderProtocol.ReadString(data, "msg", "message")
                ?? "领取成功";
        }
        catch (JsonException) { return Truncate(body); }
    }

    private static string RequireActivityId(string? id)
    {
        id = (id ?? "").Trim();
        if (id.Length is < 1 or > 200 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'))
            throw new ArgumentException("活动 ID 格式无效；请使用 /ext qoder-activities 查看。");
        return id;
    }

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static string Truncate(string? value)
    {
        value = (value ?? "").Trim();
        return value.Length > 300 ? value[..300] : value;
    }
}

/// <summary>Reads the official Qoder client's machine fingerprint when it is present.</summary>
internal static class QoderClientCache
{
    private static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qoder", "SharedClientCache");

    internal static string MachineId()
    {
        try
        {
            var path = Path.Combine(Directory, "cache", "id");
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path).Trim();
            return text.Length is > 0 and <= 256 && !text.Any(char.IsWhiteSpace) && !text.Any(char.IsControl) ? text : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    internal static (string Token, string Type) MachineToken()
    {
        try
        {
            var path = Path.Combine(Directory, "cache", "machine_token.json");
            if (!File.Exists(path)) return ("", "");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var token = QoderProtocol.ReadString(root, "token") ?? "";
            var type = QoderProtocol.ReadString(root, "type") ?? "";
            static bool Safe(string value, int limit) => value.Length <= limit && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
            return (Safe(token, 4096) ? token : "", Safe(type, 64) ? type : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ("", ""); }
    }
}
