using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanAgent.Core.Extensions;

public sealed record OAuthProfile(string Id, string Name, string ClientId, string TokenEndpoint, string? AuthorizationEndpoint = null, string? DeviceEndpoint = null, string Scope = "", string? Resource = null, bool NonExpiringAccessToken = false);
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? MetadataJson = null);

/// <summary>Windows current-user protected token storage. Secrets never enter settings or exported profiles.</summary>
public sealed class TokenVault(string directory)
{
    internal string Identity => Path.GetFullPath(directory);
    private string PathFor(string id) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin");
    internal async Task<FileStream> AcquireAsync(string id, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(PathFor(id) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(50, ct); }
        }
    }
    public async Task SaveAsync(string id, OAuthTokens tokens, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前内置 OAuth 凭据库要求 Windows DPAPI；其他平台请提供认证插件。");
        Directory.CreateDirectory(directory); var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor(id) + ".tmp", bytes, ct); File.Move(PathFor(id) + ".tmp", PathFor(id), true);
    }
    public async Task<OAuthTokens?> ReadAsync(string id, CancellationToken ct)
    {
        if (!File.Exists(PathFor(id))) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据库要求 Windows DPAPI。");
        var bytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(PathFor(id), ct), null, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<OAuthTokens>(bytes);
    }
    public Task DeleteAsync(string id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); File.Delete(PathFor(id)); return Task.CompletedTask; }
}

/// <summary>Public-client PKCE S256 and RFC8628 device flow. Profiles explicitly select the authorization server.</summary>
public sealed class OAuthAuthentication : IAuthenticationProvider
{
    private readonly OAuthProfile _profile;
    private readonly TokenVault _vault;
    private readonly HttpClient _http;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly Func<string, CancellationToken, Task>? _validateToken;
    private readonly Func<Uri, bool>? _validateVerification;
    public OAuthAuthentication(OAuthProfile profile, TokenVault vault, HttpClient? http = null,
        Func<string, CancellationToken, Task>? validateToken = null, Func<Uri, bool>? validateVerification = null)
    {
        _profile = profile; _vault = vault; _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _validateToken = validateToken; _validateVerification = validateVerification;
        _gate = Gates.GetOrAdd(vault.Identity + "\0" + profile.Id, _ => new(1, 1));
        foreach (var address in new[] { profile.TokenEndpoint, profile.AuthorizationEndpoint, profile.DeviceEndpoint }.Where(a => a != null))
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("OAuth 地址必须为 HTTPS（本地测试允许环回 HTTP）。");
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.ClientId) || profile.AuthorizationEndpoint == null && profile.DeviceEndpoint == null) throw new ArgumentException("OAuth 配置缺少 ID、Client ID 或授权入口。");
    }
    public string Id => _profile.Id;
    public string Name => _profile.Name;
    public string? LoginInstructions { get; private set; }
    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        await _gate.WaitAsync(ct); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var lease = await _vault.AcquireAsync(Id, deadline.Token);
            if (_profile.DeviceEndpoint != null) await DeviceAsync(openBrowser, deadline.Token);
            else await PkceAsync(openBrowser, deadline.Token);
        }
        finally { _gate.Release(); }
    }
    private Dictionary<string, string> Fields(params (string Key, string Value)[] values)
    {
        var fields = new Dictionary<string, string> { ["client_id"] = _profile.ClientId };
        if (_profile.Resource is { Length: > 0 }) fields["resource"] = _profile.Resource;
        foreach (var (key, value) in values) fields[key] = value; return fields;
    }
    private async Task<JsonDocument> PostAsync(string endpoint, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.Add(new("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > 64000) throw new InvalidDataException("OAuth 响应过大。"); output.Write(buffer, 0, count); }
        var doc = JsonDocument.Parse(output.ToArray());
        if (!response.IsSuccessStatusCode && !doc.RootElement.TryGetProperty("error", out _)) { doc.Dispose(); throw new InvalidOperationException("OAuth HTTP 错误：" + (int)response.StatusCode); }
        return doc;
    }
    private async Task StoreAsync(JsonElement json, CancellationToken ct, string? previousRefresh = null)
    {
        if (json.TryGetProperty("error", out var error)) throw new InvalidOperationException("OAuth 授权失败：" + error.GetString());
        if (json.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OAuth 仅支持 Bearer token。");
        var token = json.GetProperty("access_token").GetString()!; if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidDataException("无效的访问令牌。");
        if (_validateToken != null) await _validateToken(token, ct);
        var expiry = json.TryGetProperty("expires_in", out var expires) ? DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expires.GetInt32(), 1, 864000))
            : _profile.NonExpiringAccessToken ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(1);
        await _vault.SaveAsync(Id, new(token, json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : previousRefresh, expiry), ct);
    }
    private async Task DeviceAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        using var doc = await PostAsync(_profile.DeviceEndpoint!, Fields(("scope", _profile.Scope)), ct); var data = doc.RootElement;
        if (data.TryGetProperty("error", out var error)) throw new InvalidOperationException("Device OAuth：" + error.GetString());
        var verification = data.TryGetProperty("verification_uri_complete", out var complete) ? complete : data.GetProperty("verification_uri");
        LoginInstructions = "浏览器登录 · 设备码：" + data.GetProperty("user_code").GetString();
        var uri = new Uri(verification.GetString()!); if ((uri.Scheme != "https" && !uri.IsLoopback) || uri.UserInfo.Length > 0 || (_validateVerification != null && !_validateVerification(uri))) throw new InvalidDataException("无效的设备登录地址。");
        await openBrowser(uri, ct);
        using var expires = CancellationTokenSource.CreateLinkedTokenSource(ct); expires.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(data.GetProperty("expires_in").GetInt32(), 1, 300)));
        var interval = data.TryGetProperty("interval", out var wait) ? Math.Clamp(wait.GetInt32(), 1, 300) : 5;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), expires.Token);
            using var token = await PostAsync(_profile.TokenEndpoint, Fields(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", data.GetProperty("device_code").GetString()!)), expires.Token);
            if (token.RootElement.TryGetProperty("error", out var tokenError))
            {
                if (tokenError.GetString() == "authorization_pending") continue;
                if (tokenError.GetString() == "slow_down") { interval += 5; continue; }
            }
            await StoreAsync(token.RootElement, ct); return;
        }
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private async Task PkceAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32)); var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";
            var fields = Fields(("response_type", "code"), ("redirect_uri", redirect), ("scope", _profile.Scope), ("state", state), ("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))), ("code_challenge_method", "S256"));
            var separator = _profile.AuthorizationEndpoint!.Contains('?') ? "&" : "?";
            await openBrowser(new Uri(_profile.AuthorizationEndpoint + separator + string.Join('&', fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))), ct);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct); await using var stream = client.GetStream(); using var input = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = new StringBuilder(); var character = new char[1];
                try { while (line.Length <= 8192 && await input.ReadAsync(character.AsMemory(), requestTimeout.Token) > 0) { if (character[0] == '\n') break; if (character[0] != '\r') line.Append(character[0]); } }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                var request = line.ToString(); if (request.Length > 8192) continue;
                var parts = request.Split(' '); if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/callback?", StringComparison.Ordinal)) continue;
                var queryItems = new Uri(redirect + parts[1][9..]).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (Key: Uri.UnescapeDataString(p[0]), Value: Uri.UnescapeDataString(p[1]))).ToArray();
                if (queryItems.Select(p => p.Key).Distinct().Count() != queryItems.Length) continue;
                var query = queryItems.ToDictionary(p => p.Key, p => p.Value);
                var valid = query.TryGetValue("state", out var supplied) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(state));
                var body = valid ? "LanAgent authorization received. You can close this page." : "Invalid authorization state.";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);
                if (!valid) continue;
                if (query.TryGetValue("error", out var denied)) throw new InvalidOperationException("OAuth：" + denied);
                if (!query.TryGetValue("code", out var code)) throw new InvalidDataException("OAuth 回调缺少 code。");
                using var token = await PostAsync(_profile.TokenEndpoint, Fields(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirect), ("code_verifier", verifier)), ct); await StoreAsync(token.RootElement, ct); return;
            }
        }
        finally { listener.Stop(); }
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var lease = await _vault.AcquireAsync(Id, ct);
            var tokens = await _vault.ReadAsync(Id, ct); if (tokens == null) return null;
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return tokens.AccessToken;
            if (tokens.RefreshToken == null) return null;
            using var token = await PostAsync(_profile.TokenEndpoint, Fields(("grant_type", "refresh_token"), ("refresh_token", tokens.RefreshToken)), ct); await StoreAsync(token.RootElement, ct, tokens.RefreshToken);
            return (await _vault.ReadAsync(Id, ct))?.AccessToken;
        }
        finally { _gate.Release(); }
    }
    public async Task LogoutAsync(CancellationToken ct) { await _gate.WaitAsync(ct); try { using var lease = await _vault.AcquireAsync(Id, ct); await _vault.DeleteAsync(Id, ct); } finally { _gate.Release(); } }
}
