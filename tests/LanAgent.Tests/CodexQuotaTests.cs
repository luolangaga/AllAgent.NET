using System.Net;
using System.Text.Json;
using LanAgent.Core.Extensions;
using Xunit;

namespace LanAgent.Tests;

public class CodexQuotaTests
{
    [Fact]
    public void ParsesRemainingPercentAndUsesRealWindowDuration()
    {
        using var json = JsonDocument.Parse("""{"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":604800},"secondary_window":{"used_percent":80.5,"limit_window_seconds":18000}}}""");
        var items = CodexQuota.Parse(json.RootElement, "model");
        Assert.Equal(new ModelFooterItem("每周剩余", "75%"), items[0]);
        Assert.Equal(new ModelFooterItem("五小时剩余", "19.5%"), items[1]);
    }
    [Fact]
    public void ExactModelBucketWinsAndAbsentValuesAreNeverAssumedFull()
    {
        using var json = JsonDocument.Parse("""{"rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000}},"additional_rate_limits":[{"normal_model_slug":"special","rate_limit":{"primary_window":{"used_percent":90,"limit_window_seconds":18000},"secondary_window":{"limit_window_seconds":604800}}}]}""");
        var items = CodexQuota.Parse(json.RootElement, "special");
        Assert.Equal("10%", items[0].Value);
        Assert.Equal("未知", items[1].Value);
        Assert.Equal("99%", CodexQuota.Parse(json.RootElement, "other")[0].Value);
        using var empty = JsonDocument.Parse("{}");
        Assert.Contains("未返回", CodexQuota.Parse(empty.RootElement, "model")[0].Value);
    }
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchUsesAppCredentialAndFixedEndpointAndReportsDeniedAccess(HttpStatusCode status)
    {
        using var workspace = new TestWorkspace();
        var vault = new TokenVault(Path.Combine(workspace.Root, "credentials"));
        var account = new ChatGptAccount("fixture-client", "fixture-subject", null);
        await vault.SaveAsync("codex-account-index", new("", null, DateTimeOffset.MinValue,
            JsonSerializer.Serialize(new { ActiveClientId = account.ClientId, Accounts = new[] { account } })), default);
        await vault.SaveAsync("codex-registration:fixture-client", new("fixture-token", null, DateTimeOffset.UtcNow.AddHours(1),
            JsonSerializer.Serialize(new { Account = account, HostId = "fixture", IdToken = (string?)null, Scopes = Array.Empty<string>(), CodexNative = true, WorkspaceId = "fixture-workspace" })), default);
        using var http = new HttpClient(new Handler(status));
        var auth = new ChatGptAuthentication(workspace.Root, http);
        Assert.False(auth.FooterProvider.Matches(new("openai", "model", null)));
        var result = await auth.FooterProvider.FetchAsync(new("openai", "model", "codex"), default);
        Assert.Contains(status == HttpStatusCode.OK ? "75%" : "无法读取", result[0].Value);
    }
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://chatgpt.com/backend-api/wham/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-token", request.Headers.Authorization!.Parameter);
            Assert.Equal("fixture-workspace", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000}}}""") });
        }
    }
}
