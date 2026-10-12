using System.Net;
using System.Text;
using System.Text.Json;
using LanAgent.WorkBuddy;
using Xunit;

namespace LanAgent.Tests;

public class WorkBuddyPluginTests
{
    [Fact]
    public async Task LoginDiscoversModelsQueriesQuotaAndLogoutClearsToken()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "lanagent-workbuddy-test-" + Guid.NewGuid());
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = "test-account", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = "header." + payload + ".signature";
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/plugin/auth/state") return """{"code":0,"data":{"state":"state","authUrl":"https://www.codebuddy.cn/login"}}""";
            if (path == "/v2/plugin/auth/token") return JsonSerializer.Serialize(new { code = 0, data = new { accessToken = token, refreshToken = "refresh" } });
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-account", request.Headers.GetValues("X-User-Id").Single());
            if (path == "/v3/config") return """{"code":0,"data":{"agents":[{"models":["deepseek","deepseek","qwen"]}]}}""";
            Assert.Equal("www.codebuddy.cn", request.RequestUri.Host);
            return """{"code":0,"data":{"Response":{"Data":{"Accounts":[{"CycleCapacityRemainPrecise":"12.5","CycleCapacityUsed":7.5}]}}}}""";
        }));
        using var account = new WorkBuddyAccount(directory, http: http);
        try
        {
            Uri? browser = null;
            await account.LoginAsync((uri, _) => { browser = uri; return Task.CompletedTask; }, default);
            Assert.Equal("www.codebuddy.cn", browser!.Host);
            Assert.Equal(token, await account.GetAccessTokenAsync(default));
            var models = await account.ModelProvider.ListModelsAsync(account.CreateConfiguration());
            Assert.Equal(2, models.Count);
            Assert.Contains("12.5", await account.QuotaAsync(default));
            await account.LogoutAsync(default);
            Assert.Null(await account.GetAccessTokenAsync(default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RejectsCredentialBindingToForeignEndpoint()
    {
        using var account = new WorkBuddyAccount(Path.GetTempPath());
        var config = account.CreateConfiguration();
        config.BaseUrl = "https://example.com/v2";
        Assert.Throws<InvalidOperationException>(() => account.ValidateModelConfiguration(config));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
    }
}
