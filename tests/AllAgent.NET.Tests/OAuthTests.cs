using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class OAuthTests
{
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(v => v.Split('=', 2)).ToDictionary(v => Uri.UnescapeDataString(v[0]), v => Uri.UnescapeDataString(v[1]));
    [Fact]
    public async Task PkceRejectsWrongStateExchangesVerifierRefreshesAndProtectsTokens()
    {
        using var w = new TestWorkspace(); string? challenge = null; var refreshes = 0; var exchanges = 0;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            var form = await context.Request.ReadFormAsync(); context.Response.ContentType = "application/json";
            if (form["grant_type"] == "authorization_code")
            {
                exchanges++; Assert.Equal("fixture-code", form["code"]); var verifier = form["code_verifier"].ToString(); Assert.InRange(verifier.Length, 43, 128);
                Assert.Equal(challenge, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                await context.Response.WriteAsync("""{"access_token":"access-secret-proof","refresh_token":"refresh-secret-proof","token_type":"Bearer","expires_in":1}""");
            }
            else
            { refreshes++; Assert.Equal("refresh-secret-proof", form["refresh_token"]); await context.Response.WriteAsync("""{"access_token":"refreshed-secret-proof","token_type":"Bearer","expires_in":3600}"""); }
        });
        var vault = new TokenVault(Path.Combine(w.Root, "vault")); var auth = new OAuthAuthentication(new("test", "test", "public-client", server.Url + "/token", server.Url + "/authorize", Scope: "scope", Resource: server.Url), vault);
        Task? callback = null;
        using var deadline = new CancellationTokenSource(15000);
        await auth.LoginAsync((uri, ct) =>
        {
            var query = Query(uri); challenge = query["code_challenge"]; Assert.Equal("S256", query["code_challenge_method"]);
            callback = Task.Run(async () =>
            {
                using var http = new HttpClient(); var redirect = query["redirect_uri"];
                using var denied = await http.GetAsync(redirect + "?state=wrong&code=stolen", ct); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Equal(0, exchanges);
                using var accepted = await http.GetAsync(redirect + "?state=" + query["state"] + "&code=fixture-code", ct); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }, ct); return Task.CompletedTask;
        }, deadline.Token);
        await callback!; Assert.Equal(1, exchanges); Assert.Equal("refreshed-secret-proof", await auth.GetAccessTokenAsync(default)); Assert.Equal(1, refreshes);
        var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(w.Root, "vault"), "*.bin"))); Assert.DoesNotContain("secret-proof", Encoding.UTF8.GetString(bytes));
        Assert.Equal("refreshed-secret-proof", (await vault.ReadAsync("test", default))!.AccessToken); await auth.LogoutAsync(default); Assert.Null(await auth.GetAccessTokenAsync(default));
    }
    [Fact]
    public async Task DeviceFlowSupportsPlainVerificationUriPendingAndCancellation()
    {
        using var w = new TestWorkspace(); var polls = 0; string? url = null;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            if (context.Request.Path == "/device") { await context.Response.WriteAsync("""{"device_code":"device-secret","user_code":"ABCD-EFGH","verification_uri":"https://fixture.example/verify","expires_in":60,"interval":1}"""); return; }
            var form = await context.Request.ReadFormAsync(); Assert.Equal("device-secret", form["device_code"]); polls++;
            if (polls == 1) { context.Response.StatusCode = 400; await context.Response.WriteAsync("""{"error":"authorization_pending"}"""); }
            else await context.Response.WriteAsync("""{"access_token":"device-access","token_type":"Bearer","expires_in":3600}""");
        });
        var auth = new OAuthAuthentication(new("device", "device", "client", server.Url + "/token", DeviceEndpoint: server.Url + "/device"), new TokenVault(Path.Combine(w.Root, "vault")));
        await auth.LoginAsync((uri, _) => { url = uri.AbsoluteUri; Assert.Contains("ABCD-EFGH", auth.LoginInstructions); return Task.CompletedTask; }, default);
        Assert.Equal("https://fixture.example/verify", url); Assert.Equal(2, polls); Assert.Equal("device-access", await auth.GetAccessTokenAsync(default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.LoginAsync((_, _) => Task.CompletedTask, cancelled.Token));
    }
    [Fact]
    public async Task AuthenticationHandlerRefusesCrossOriginBeforeReadingToken()
    {
        var authentication = new FixtureAuthentication(); using var client = new HttpClient(new AuthenticationHttpHandler(authentication, new Uri("https://allowed.example/mcp")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://other.example/mcp")); Assert.Equal(0, authentication.Reads);
    }
    internal sealed class FixtureAuthentication : IAuthenticationProvider
    {
        public int Reads;
        public string Id => "fixture"; public string Name => "Fixture";
        public Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) { Reads++; return Task.FromResult<string?>("fixture"); }
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
