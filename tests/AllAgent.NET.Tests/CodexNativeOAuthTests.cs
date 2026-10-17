using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class CodexNativeOAuthTests
{
    [Fact]
    public async Task NativeLoginExchangesPkceThenRoutesInferenceAndQuotaWithSameWorkspace()
    {
        using var workspace = new TestWorkspace();
        using var handler = new OAuthHandler();
        using var http = new HttpClient(handler);
        var auth = new ChatGptAuthentication(workspace.Root, http);
        await auth.LoginAsync(async (uri, ct) =>
        {
            Assert.Equal("/oauth/authorize", uri.AbsolutePath);
            handler.Query = Query(uri.Query);
            Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", handler.Query["client_id"]);
            Assert.False(handler.Query.ContainsKey("resource"));
            var redirect = handler.Query["redirect_uri"];
            Assert.Contains(new Uri(redirect).Port, new[] { 1455, 1457 });
            var callback = new HttpClient();
            // The browser callback is processed after this browser-launch delegate returns.
            handler.Callback = callback.GetAsync(redirect + "?code=fixture-code&state=" + handler.Query["state"], ct);
            await Task.CompletedTask;
            handler.CallbackClient = callback; // Keep the callback client alive outside this delegate.
        }, default);
        var credential = await auth.GetCredentialAsync(default);
        Assert.Equal(1, handler.Refreshes);
        Assert.Equal("workspace-A", credential!.AccountId);
        Assert.Equal(ChatGptAuthentication.CodexEndpoint, credential.InferenceBaseUrl);
        var config = new ApiConfig { ProviderId = "codex", Protocol = "responses", BaseUrl = "https://api.openai.com/v1", DefaultModel = "fixture", AuthenticationProviderId = "codex" };
        var provider = new AuthenticatedAiProvider(new ResponsesProvider(http), auth);
        Assert.Single(await provider.ListModelsAsync(config));
        var quota = await auth.FooterProvider.FetchAsync(new("codex", "fixture", "codex"), default);
        Assert.Equal("75%", quota[0].Value);
        await auth.LogoutAsync(default);
        Assert.Null(await auth.GetCredentialAsync(default));
        Assert.True(handler.Revoked);
    }
    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    private sealed class OAuthHandler : HttpMessageHandler
    {
        private readonly RSA rsa = RSA.Create(2048);
        public Dictionary<string, string> Query = [];
        public Task<HttpResponseMessage>? Callback;
        public HttpClient? CallbackClient;
        public bool Revoked;
        public int Refreshes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth/token")
            {
                var form = CodexNativeOAuthTests.Query(await request.Content!.ReadAsStringAsync(ct));
                if (form["grant_type"] == "authorization_code")
                {
                    Assert.Equal("fixture-code", form["code"]);
                    Assert.Equal(Query["code_challenge"], B64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
                }
                else
                {
                    Assert.Equal("refresh_token", form["grant_type"]);
                    Assert.Equal("native-refresh", form["refresh_token"]);
                    Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", form["client_id"]);
                    Refreshes++;
                }
                Assert.False(form.ContainsKey("resource"));
                var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" }));
                var claims = new Dictionary<string, object> { ["iss"] = "https://auth.openai.com", ["aud"] = Query["client_id"], ["sub"] = "subject-A", ["nonce"] = Query["nonce"], ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["https://api.openai.com/auth"] = new { chatgpt_account_id = "workspace-A" } };
                var body = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
                var signed = header + "." + body;
                var token = signed + "." + B64(rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                return Json(new { access_token = "native-access", refresh_token = "native-refresh", id_token = token, expires_in = Refreshes == 0 ? 1 : 3600 });
            }
            if (path == "/.well-known/openid-configuration") return Json(new { issuer = "https://auth.openai.com", jwks_uri = "https://auth.openai.com/jwks" });
            if (path == "/jwks")
            {
                var key = rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", alg = "RS256", kid = "fixture", n = B64(key.Modulus!), e = B64(key.Exponent!) } } });
            }
            if (path == "/oauth/revoke")
            {
                var form = CodexNativeOAuthTests.Query(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", form["client_id"]);
                Revoked = true;
                return new(HttpStatusCode.OK);
            }
            Assert.Equal("chatgpt.com", request.RequestUri.Host);
            Assert.Equal("native-access", request.Headers.Authorization!.Parameter);
            Assert.Equal("workspace-A", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            if (path == "/backend-api/codex/models") return Json(new { models = new[] { new { slug = "fixture", visibility = "list", display_name = "Fixture" } } });
            Assert.Equal("/backend-api/wham/usage", path);
            return Json(new { rate_limit = new { primary_window = new { used_percent = 25, limit_window_seconds = 18000 } } });
        }
        private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
        protected override void Dispose(bool disposing) { rsa.Dispose(); CallbackClient?.Dispose(); base.Dispose(disposing); }
    }
}
