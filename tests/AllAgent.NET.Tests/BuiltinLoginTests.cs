using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class BuiltinLoginTests
{
    [Fact]
    public void LoginCommandsReplaceAuthWizard()
    {
        Assert.Contains(CommandCatalog.Builtins, c => c.Text == "/login" && c.Usage.Contains("提供商"));
        Assert.Contains(CommandCatalog.Builtins, c => c.Text == "/login status");
        Assert.DoesNotContain(CommandCatalog.Builtins, c => c.Text.Contains("copilot", StringComparison.OrdinalIgnoreCase) || c.Text.Contains("claudecode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CommandCatalog.Builtins, c => c.Text.StartsWith("/auth", StringComparison.Ordinal));
    }
    [Fact]
    public async Task NativeLoginVerifiesIdentityRefreshesAndRetainsMappingAfterLogout()
    {
        using var workspace = new TestWorkspace(); using var fixture = new ChatGptFixture();
        var login = new ChatGptAuthentication(workspace.Root, fixture.Http);
        await fixture.LoginAsync(login); Assert.Equal(ChatGptAuthentication.NativeClientId, fixture.Authorization!["client_id"]);
        var account = Assert.Single(await login.GetAccountsAsync(default)); Assert.StartsWith("native-", account.ClientId);
        var hostId = File.ReadAllText(Path.Combine(workspace.Root, "chatgpt-host-id"));
        Assert.Equal("fixture-access", await login.GetAccessTokenAsync(default));
        await fixture.LoginAsync(login); Assert.Equal(ChatGptAuthentication.NativeClientId, fixture.Authorization!["client_id"]);
        Assert.Equal(account.ClientId, Assert.Single(await login.GetAccountsAsync(default)).ClientId);
        Assert.Equal(hostId, File.ReadAllText(Path.Combine(workspace.Root, "chatgpt-host-id")));
        await login.LogoutAsync(default); Assert.True(fixture.Revoked); Assert.Null(await login.GetAccessTokenAsync(default)); Assert.Single(await login.GetAccountsAsync(default));
        var next = new ChatGptAuthentication(workspace.Root, fixture.Http); await fixture.LoginAsync(next);
        Assert.Equal(ChatGptAuthentication.NativeClientId, fixture.Authorization!["client_id"]);
        Assert.Equal(account.ClientId, Assert.Single(await next.GetAccountsAsync(default)).ClientId);
        Assert.Throws<InvalidOperationException>(() => login.ValidateModelConfiguration(new ApiConfig { Protocol = "responses", BaseUrl = "https://attacker.example/v1" }));
        var vault = new TokenVault(Path.Combine(workspace.Root, "credentials")); var key = "codex-registration:" + account.ClientId; var tokens = (await vault.ReadAsync(key, default))!;
        await vault.SaveAsync(key, tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        Assert.Equal("fixture-refreshed", await next.GetAccessTokenAsync(default)); Assert.Equal(1, fixture.Refreshes);
        Assert.Equal("fixture-rotated-refresh", (await vault.ReadAsync(key, default))!.RefreshToken);
    }
    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("subject")]
    [InlineData("refresh")]
    [InlineData("workspace")]
    public async Task ChatGptRejectsInvalidIdentityAndGrantWithoutOverwritingCredentials(string failure)
    {
        using var workspace = new TestWorkspace(); using var fixture = new ChatGptFixture(); var login = new ChatGptAuthentication(workspace.Root, fixture.Http);
        await fixture.LoginAsync(login);
        var account = Assert.Single(await login.GetAccountsAsync(default));
        var vault = new TokenVault(Path.Combine(workspace.Root, "credentials")); var key = "codex-registration:" + account.ClientId;
        if (failure == "subject")
        {
            var saved = (await vault.ReadAsync(key, default))!;
            await vault.SaveAsync(key, saved with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        }
        fixture.Failure = failure;
        if (failure == "subject") await Assert.ThrowsAnyAsync<Exception>(() => login.GetAccessTokenAsync(default));
        else await Assert.ThrowsAnyAsync<Exception>(() => fixture.LoginAsync(login));
        Assert.Equal("fixture-access", (await vault.ReadAsync(key, default))!.AccessToken);
        Assert.Equal(account, Assert.Single(await login.GetAccountsAsync(default)));
    }
    private static Dictionary<string, string> Parse(string value) => value.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request); }
    private sealed class ChatGptFixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public readonly HttpClient Http;
        public Dictionary<string, string>? Authorization;
        public string? Failure;
        public int Refreshes;
        public bool Revoked;
        public ChatGptFixture() { Http = new HttpClient(new Handler(HandleAsync)); }
        public async Task LoginAsync(ChatGptAuthentication login)
        {
            Task? callback = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await login.LoginAsync((uri, ct) =>
                {
                    Authorization = Parse(uri.Query); Assert.Equal("https://auth.openai.com/oauth/authorize", uri.GetLeftPart(UriPartial.Path)); Assert.Equal("S256", Authorization["code_challenge_method"]);
                    var redirect = Authorization["redirect_uri"]; Assert.Equal("/auth/callback", new Uri(redirect).AbsolutePath);
                    callback = Task.Run(async () =>
                    {
                        using var client = new HttpClient();
                        using var wrong = await client.GetAsync(redirect + "?state=wrong&code=stolen", ct); Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
                        using var good = await client.GetAsync(redirect + "?state=" + Authorization["state"] + "&code=fixture-code&client_id=oaiapp_fixture", ct); Assert.Equal(HttpStatusCode.OK, good.StatusCode);
                    }, ct);
                    return Task.CompletedTask;
                }, deadline.Token);
            }
            finally { if (callback != null) await callback; }
        }
        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            Assert.Equal("auth.openai.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("openid-configuration")) return Json("""{"issuer":"https://auth.openai.com","jwks_uri":"https://auth.openai.com/.well-known/jwks.json","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""");
            if (path.EndsWith("jwks.json")) { var key = _rsa.ExportParameters(false); return Json(JsonSerializer.Serialize(new { keys = new[] { new { kid = "fixture", kty = "RSA", alg = "RS256", use = "sig", n = B64(key.Modulus!), e = B64(key.Exponent!) } } })); }
            var form = Parse(await request.Content!.ReadAsStringAsync()); Assert.Equal(ChatGptAuthentication.NativeClientId, form["client_id"]); Assert.False(form.ContainsKey("client_secret"));
            if (path.EndsWith("revoke")) { Assert.Equal("refresh_token", form["token_type_hint"]); Revoked = true; return new(HttpStatusCode.OK); }
            Assert.False(form.ContainsKey("resource"));
            var refresh = form["grant_type"] == "refresh_token";
            if (refresh) { Refreshes++; Assert.Equal("fixture-refresh", form["refresh_token"]); }
            else { Assert.Equal(Authorization!["redirect_uri"], form["redirect_uri"]); Assert.Equal(Authorization["code_challenge"], B64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])))); }
            var claims = new Dictionary<string, object>
            {
                ["iss"] = Failure == "issuer" ? "https://attacker.example" : "https://auth.openai.com",
                ["aud"] = Failure == "audience" ? "wrong-client" : ChatGptAuthentication.NativeClientId,
                ["sub"] = Failure == "subject" ? "other-subject" : "subject_fixture",
                ["email"] = "fixture@example.com", ["nonce"] = Failure == "nonce" ? "wrong-nonce" : Authorization!["nonce"],
                ["exp"] = DateTimeOffset.UtcNow.AddMinutes(Failure == "expired" ? -1 : 10).ToUnixTimeSeconds(),
                ["https://api.openai.com/auth"] = new { chatgpt_account_id = Failure == "workspace" ? "" : "fixture-workspace" }
            };
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signature = _rsa.SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); if (Failure == "signature") signature[0] ^= 1;
            return Json(JsonSerializer.Serialize(new { access_token = refresh ? "fixture-refreshed" : "fixture-access", refresh_token = Failure == "refresh" ? "" : refresh ? "fixture-rotated-refresh" : "fixture-refresh", token_type = "Bearer", expires_in = 3600, id_token = payload + "." + B64(signature) }));
        }
        private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public void Dispose() { Http.Dispose(); _rsa.Dispose(); }
    }
}
