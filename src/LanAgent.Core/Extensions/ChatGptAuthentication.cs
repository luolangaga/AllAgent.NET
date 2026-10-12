using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanAgent.Core.Models;

namespace LanAgent.Core.Extensions;

public sealed record ChatGptAccount(string ClientId, string Subject, string? Email);
internal sealed record ChatGptRegistration(ChatGptAccount Account, string HostId, string? IdToken, string[] Scopes);
internal sealed record ChatGptAccountIndex(string? ActiveClientId, ChatGptAccount[] Accounts);

/// <summary>Public-client Sign in with ChatGPT for open-source, locally hosted agents.</summary>
public sealed class ChatGptAuthentication : IAuthenticationProvider
{
    private const string Issuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
    private const string IndexKey = "codex-account-index";
    private readonly TokenVault _vault;
    private readonly HttpClient _http;
    private readonly string _hostId;
    public ChatGptAuthentication(string dataDirectory, HttpClient? http = null)
    {
        _vault = new(Path.Combine(dataDirectory, "credentials"));
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "chatgpt-host-id");
        try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString())); }
        catch (IOException) when (File.Exists(path)) { }
        if (!Guid.TryParse(File.ReadAllText(path), out var host) || host == Guid.Empty) throw new InvalidDataException("ChatGPT 主机标识无效；请修复 chatgpt-host-id。");
        _hostId = "urn:uuid:" + host;
    }
    public string Id => "codex";
    public string Name => "Codex · Continue with ChatGPT";
    public string? LoginInstructions { get; private set; }
    public void ValidateModelConfiguration(ApiConfig config)
    {
        if (config.Protocol != "responses" || !Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) || uri.AbsoluteUri.TrimEnd('/') != Resource || !string.IsNullOrEmpty(config.ExtraHeadersJson))
            throw new InvalidOperationException("ChatGPT 账号必须使用官方 https://api.openai.com/v1 Responses 接口，不能向自定义地址或附加认证头发送令牌。");
    }
    private static string Key(string clientId) => "codex-registration:" + clientId;
    private async Task<ChatGptAccountIndex> IndexAsync(CancellationToken ct) =>
        (await _vault.ReadAsync(IndexKey, ct))?.MetadataJson is { } json ? JsonSerializer.Deserialize<ChatGptAccountIndex>(json)! : new(null, []);
    private Task SaveIndexAsync(ChatGptAccountIndex index, CancellationToken ct) => _vault.SaveAsync(IndexKey, new("", null, DateTimeOffset.MinValue, JsonSerializer.Serialize(index)), ct);
    private static ChatGptRegistration Registration(OAuthTokens tokens) => JsonSerializer.Deserialize<ChatGptRegistration>(tokens.MetadataJson ?? throw new InvalidDataException("ChatGPT 账号注册记录缺失。"))!;
    public async Task<IReadOnlyList<ChatGptAccount>> GetAccountsAsync(CancellationToken ct) => (await IndexAsync(ct)).Accounts;
    public async Task SelectAccountAsync(string clientId, CancellationToken ct)
    {
        using var lease = await _vault.AcquireAsync(IndexKey, ct);
        var index = await IndexAsync(ct);
        if (!index.Accounts.Any(a => a.ClientId == clientId)) throw new ArgumentException("ChatGPT 账号不存在。");
        await SaveIndexAsync(index with { ActiveClientId = clientId }, ct);
    }
    public Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct) => LoginAsync(openBrowser, false, ct);
    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, bool newAccount, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var lease = await _vault.AcquireAsync(IndexKey, deadline.Token);
        var index = await IndexAsync(deadline.Token);
        var previous = !newAccount && index.ActiveClientId is { } active ? await _vault.ReadAsync(Key(active), deadline.Token) : null;
        var registration = previous == null ? null : Registration(previous);
        var clientId = registration?.Account.ClientId ?? "dynamic_agent_client";
        var state = Base64(RandomNumberGenerator.GetBytes(32)); var nonce = Base64(RandomNumberGenerator.GetBytes(32)); var verifier = Base64(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
            var fields = new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["ext_agent_host_id"] = _hostId, ["response_type"] = "code", ["redirect_uri"] = redirect,
                ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", ["resource"] = Resource,
                ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            if (registration == null) fields["agent_name_hint"] = "LanAgent";
            else if (registration.IdToken is { Length: > 0 } hint) fields["id_token_hint"] = hint;
            LoginInstructions = "在浏览器中选择 ChatGPT 账号并授权 LanAgent";
            await openBrowser(new Uri(Issuer + "/api/accounts/authorize?" + string.Join('&', fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))), deadline.Token);
            var callback = await CallbackAsync(listener, redirect, state, deadline.Token);
            if (callback.TryGetValue("error", out var error)) throw new InvalidOperationException("ChatGPT 授权未完成：" + error);
            if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("ChatGPT 回调缺少授权码。");
            if (registration == null)
            {
                clientId = callback.GetValueOrDefault("client_id") ?? "";
                if (string.IsNullOrWhiteSpace(clientId) || clientId == "dynamic_agent_client" || clientId.Any(char.IsControl)) throw new InvalidDataException("ChatGPT 尚未返回注册后的 Client ID。");
            }
            else if (callback.TryGetValue("client_id", out var supplied) && supplied != clientId) throw new InvalidDataException("ChatGPT 回调与所选账号的 Client ID 不匹配。");
            using var token = await PostAsync(TokenEndpoint, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource }, deadline.Token);
            var identity = await ValidateIdentityAsync(Required(token.RootElement, "id_token"), clientId, nonce, deadline.Token);
            var account = new ChatGptAccount(clientId, Required(identity, "sub"), identity.TryGetProperty("email", out var email) ? email.GetString() : null);
            if (registration != null && registration.Account.Subject != account.Subject) throw new InvalidDataException("ChatGPT 返回的身份不属于所选账号，旧凭据未修改。");
            var existing = index.Accounts.FirstOrDefault(a => a.ClientId == clientId);
            if (existing != null && existing.Subject != account.Subject) throw new InvalidDataException("ChatGPT 注册身份冲突，旧凭据未修改。");
            var saved = Credentials(token.RootElement, new(account, _hostId, Required(token.RootElement, "id_token"), []));
            await _vault.SaveAsync(Key(clientId), saved, deadline.Token);
            await SaveIndexAsync(new(clientId, index.Accounts.Where(a => a.ClientId != clientId).Append(account).ToArray()), deadline.Token);
        }
        finally { listener.Stop(); }
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct) => (await GetCredentialAsync(ct))?.AccessToken;
    public async Task<AuthenticationCredential?> GetCredentialAsync(CancellationToken ct)
    {
        using var lease = await _vault.AcquireAsync(IndexKey, ct);
        var index = await IndexAsync(ct);
        if (index.ActiveClientId == null) return null;
        var saved = await _vault.ReadAsync(Key(index.ActiveClientId), ct);
        if (saved == null || string.IsNullOrEmpty(saved.AccessToken)) return null;
        var registration = Registration(saved);
        if (!registration.Scopes.Contains("chatgpt.tokens.use.direct")) throw new InvalidOperationException("未授予 ChatGPT 模型使用权限，请重新 /login codex。");
        var contextId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registration.Account.ClientId + "\0" + registration.Account.Subject)));
        if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return new(saved.AccessToken, contextId);
        if (saved.RefreshToken == null) return null;
        using var response = await PostAsync(TokenEndpoint, new() { ["grant_type"] = "refresh_token", ["client_id"] = registration.Account.ClientId, ["refresh_token"] = saved.RefreshToken, ["resource"] = Resource }, ct);
        if (response.RootElement.TryGetProperty("id_token", out var idToken))
        {
            var identity = await ValidateIdentityAsync(idToken.GetString()!, registration.Account.ClientId, null, ct);
            if (Required(identity, "sub") != registration.Account.Subject) throw new InvalidDataException("刷新后的 ChatGPT 身份不匹配。");
            registration = registration with { IdToken = idToken.GetString() };
        }
        var next = Credentials(response.RootElement, registration);
        await _vault.SaveAsync(Key(registration.Account.ClientId), next, ct);
        return new(next.AccessToken, contextId);
    }
    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        var index = await IndexAsync(ct); var account = index.Accounts.FirstOrDefault(a => a.ClientId == index.ActiveClientId);
        var token = await GetAccessTokenAsync(ct);
        return new(token != null, true, account?.Email ?? account?.ClientId, token == null ? "使用 /login codex 继续登录。" : "ChatGPT 身份与模型使用授权已验证；模型目录与实际请求另行检查。");
    }
    public async Task LogoutAsync(CancellationToken ct)
    {
        using var lease = await _vault.AcquireAsync(IndexKey, ct);
        var index = await IndexAsync(ct);
        if (index.ActiveClientId == null) return;
        var tokens = await _vault.ReadAsync(Key(index.ActiveClientId), ct);
        if (tokens == null) return;
        var registration = Registration(tokens); Exception? revokeError = null;
        try
        {
            if (tokens.RefreshToken != null)
            {
                using var metadata = await GetAsync(Issuer + "/.well-known/openid-configuration", ct);
                var endpoint = SameIssuerEndpoint(Required(metadata.RootElement, "revocation_endpoint"));
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = registration.Account.ClientId }) };
                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("未确认服务端会话撤销。");
            }
        }
        catch (Exception ex) { revokeError = ex; }
        await _vault.SaveAsync(Key(index.ActiveClientId), new("", null, DateTimeOffset.MinValue, JsonSerializer.Serialize(registration with { IdToken = null, Scopes = [] })), CancellationToken.None);
        if (revokeError != null) throw new InvalidOperationException("已清除本地 ChatGPT 凭据；服务端撤销未确认，请在 ChatGPT 设置中断开 LanAgent。", revokeError);
    }
    private static OAuthTokens Credentials(JsonElement response, ChatGptRegistration registration)
    {
        if (!Required(response, "token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ChatGPT 返回了不支持的令牌类型。");
        var scopes = Required(response, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("chatgpt.tokens.use.direct")) throw new InvalidOperationException("账号未授权 ChatGPT 模型使用，旧凭据未修改。");
        var expiry = response.GetProperty("expires_in").GetInt32();
        if (expiry <= 0 || expiry > 86400) throw new InvalidDataException("ChatGPT 令牌有效期无效。");
        return new(Required(response, "access_token"), Required(response, "refresh_token"), DateTimeOffset.UtcNow.AddSeconds(expiry), JsonSerializer.Serialize(registration with { Scopes = scopes }));
    }
    private async Task<JsonElement> ValidateIdentityAsync(string token, string clientId, string? nonce, CancellationToken ct)
    {
        var segments = token.Split('.'); if (segments.Length != 3 || token.Length > 32000) throw new InvalidDataException("ChatGPT ID token 格式无效。");
        using var header = JsonDocument.Parse(Unbase64(segments[0])); using var claims = JsonDocument.Parse(Unbase64(segments[1]));
        if (Required(header.RootElement, "alg") != "RS256") throw new InvalidDataException("ChatGPT ID token 签名算法无效。");
        using var metadata = await GetAsync(Issuer + "/.well-known/openid-configuration", ct);
        if (Required(metadata.RootElement, "issuer") != Issuer) throw new InvalidDataException("OpenAI 认证元数据 issuer 无效。");
        using var keys = await GetAsync(SameIssuerEndpoint(Required(metadata.RootElement, "jwks_uri")), ct);
        var kid = Required(header.RootElement, "kid"); var matches = keys.RootElement.GetProperty("keys").EnumerateArray().Where(k => k.TryGetProperty("kid", out var keyId) && keyId.GetString() == kid).ToArray();
        if (matches.Length != 1 || Required(matches[0], "kty") != "RSA" || (matches[0].TryGetProperty("use", out var use) && use.GetString() != "sig") || (matches[0].TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")) throw new InvalidDataException("未找到可信的 ChatGPT 签名公钥。");
        using var rsa = RSA.Create(); rsa.ImportParameters(new() { Modulus = Unbase64(Required(matches[0], "n")), Exponent = Unbase64(Required(matches[0], "e")) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]), Unbase64(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("ChatGPT ID token 签名无效。");
        var identity = claims.RootElement; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var aud = identity.GetProperty("aud"); var audienceMatches = aud.ValueKind == JsonValueKind.String ? aud.GetString() == clientId : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(a => a.GetString() == clientId);
        if (aud.ValueKind == JsonValueKind.Array && aud.GetArrayLength() > 1 && Required(identity, "azp") != clientId) throw new InvalidDataException("ChatGPT ID token 的授权客户端不匹配。");
        if (Required(identity, "iss") != Issuer || !audienceMatches || identity.GetProperty("exp").GetInt64() <= now || (identity.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 30) || (identity.TryGetProperty("iat", out var iat) && iat.GetInt64() > now + 30)) throw new InvalidDataException("ChatGPT ID token 的来源、受众或有效期无效。");
        if (nonce != null && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Required(identity, "nonce")), Encoding.UTF8.GetBytes(nonce))) throw new InvalidDataException("ChatGPT ID token nonce 不匹配。");
        _ = Required(identity, "sub"); return identity.Clone();
    }
    private static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && !text.Any(char.IsControl) ? text : throw new InvalidDataException("认证响应缺少有效字段：" + name);
    private static string SameIssuerEndpoint(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "auth.openai.com" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? address : throw new InvalidDataException("拒绝使用外部认证端点。");
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Unbase64(string text) { var value = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    private async Task<JsonDocument> GetAsync(string address, CancellationToken ct)
    {
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("认证元数据请求失败（HTTP " + (int)response.StatusCode + "）。");
        return await ReadAsync(response, ct);
    }
    private async Task<JsonDocument> PostAsync(string endpoint, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var response = await _http.PostAsync(endpoint, new FormUrlEncodedContent(fields), ct);
        var json = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode || json.RootElement.TryGetProperty("error", out _)) { var reason = json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "HTTP " + (int)response.StatusCode; json.Dispose(); throw new InvalidOperationException("ChatGPT 认证失败：" + reason + "；请重新 /login codex。"); }
        return json;
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var result = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (result.Length + count > 128000) throw new InvalidDataException("认证响应过大。"); result.Write(buffer, 0, count); }
        return JsonDocument.Parse(result.ToArray());
    }
    private static async Task<Dictionary<string, string>> CallbackAsync(TcpListener listener, string redirect, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct); await using var stream = client.GetStream();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var input = new StreamReader(stream, Encoding.ASCII, false, 1024, true); var line = new StringBuilder(); var character = new char[1];
            try { while (line.Length <= 8192 && await input.ReadAsync(character.AsMemory(), deadline.Token) > 0) { if (character[0] == '\n') break; if (character[0] != '\r') line.Append(character[0]); } }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            var parts = line.ToString().Split(' '); Dictionary<string, string>? query = null;
            if (line.Length <= 8192 && parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/auth/callback?", StringComparison.Ordinal))
            {
                try { query = new Uri(redirect + parts[1][14..]).Query.TrimStart('?').Split('&').Select(v => v.Split('=', 2)).Where(v => v.Length == 2).ToDictionary(v => Uri.UnescapeDataString(v[0].Replace('+', ' ')), v => Uri.UnescapeDataString(v[1].Replace('+', ' '))); }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException) { }
            }
            var valid = query != null && query.TryGetValue("state", out var supplied) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(state));
            var body = Encoding.UTF8.GetBytes(valid ? "LanAgent received your sign-in. Return to the terminal to check the result." : "Invalid authorization request.");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct); await stream.WriteAsync(body, ct);
            if (valid) return query!;
        }
    }
}
