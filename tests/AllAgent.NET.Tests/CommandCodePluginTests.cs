using System.Reflection;
using AllAgent.NET.CommandCode;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public class CommandCodePluginTests
{
    [Fact]
    public void RejectsCredentialBindingToForeignEndpointOrProtocol()
    {
        using var account = new CommandCodeAccount(Path.GetTempPath());
        var config = account.CreateConfiguration();
        account.ValidateModelConfiguration(config);
        config.BaseUrl = "https://example.com";
        Assert.Throws<InvalidOperationException>(() => account.ValidateModelConfiguration(config));
        config.BaseUrl = CommandCodeAccount.Endpoint;
        config.Protocol = "openai";
        Assert.Throws<InvalidOperationException>(() => account.ValidateModelConfiguration(config));
    }

    [Fact]
    public async Task NativeLoginAndChatTranslateProtocolWithoutRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "commandcode-test-" + Guid.NewGuid());
        var previous = Environment.GetEnvironmentVariable("COMMANDCODE_API_KEY");
        Environment.SetEnvironmentVariable("COMMANDCODE_API_KEY", "user_test");
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("user_test", request.Headers.Authorization!.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("models")) return """{"data":[{"id":"test/model"}]}""";
            if (path.EndsWith("generate"))
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("test/model", body["params"]!["model"]!.GetValue<string>());
                Assert.Equal("execute_code", body["params"]!["tools"]![0]!["name"]!.GetValue<string>());
                return """
                    {"type":"reasoning-delta","text":"thinking"}
                    {"type":"text-delta","text":"hello"}
                    {"type":"tool-call","toolCallId":"call_1","toolName":"execute_code","input":{"code":"print(1)"}}
                    {"type":"finish","finishReason":"tool-calls","totalUsage":{"inputTokens":10,"outputTokens":3,"cachedInputTokens":2}}
                    """;
            }
            return "{}";
        }));
        using var account = new CommandCodeAccount(directory, client);
        try
        {
            await account.LoginAsync((_, _) => throw new Exception("No browser required"), default);
            var config = account.CreateConfiguration(); config.DefaultModel = "test/model";
            var request = new AllAgent.NET.Core.Services.AI.ChatRequest(config, [new("system", "system"), new("user", "hello")], [new("execute_code", "execute", new { type = "object" })]);
            var chunks = new List<AllAgent.NET.Core.Services.AI.StreamChunk>();
            await foreach (var chunk in account.ModelProvider.StreamChatAsync(request)) chunks.Add(chunk);
            Assert.Contains(chunks, c => c.Type == AllAgent.NET.Core.Services.AI.ChunkType.ThinkingDelta);
            Assert.Contains(chunks, c => c.ToolCall?.Name == "execute_code");
            Assert.Contains(chunks, c => c.Usage?.CachedInputTokens == 2);
            Assert.Equal("tool_calls", chunks.Last().FinishReason);
            Assert.Contains("/alpha/fingerprint/record", requests);
            Assert.Contains("/alpha/lifecycle-events", requests);
            await account.LogoutAsync(default);
            Assert.Null(await account.GetAccessTokenAsync(default));
        }
        finally { Environment.SetEnvironmentVariable("COMMANDCODE_API_KEY", previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"type\":\"text-delta\",\"text\":\"partial\"}")]
    [InlineData("{\"type\":\"finish\",\"finishReason\":\"stop\"}")]
    [InlineData("{\"type\":\"error\",\"message\":\"secret\"}")]
    [InlineData("invalid json")]
    public async Task IncompleteEmptyAndFailedStreamsReturnError(string data)
    {
        var chunks = new List<AllAgent.NET.Core.Services.AI.StreamChunk>();
        await foreach (var chunk in CommandCodeProvider.ParseStreamAsync(new StringReader(data))) chunks.Add(chunk);
        Assert.Equal(AllAgent.NET.Core.Services.AI.ChunkType.Error, chunks.Last().Type);
        Assert.DoesNotContain(chunks, c => c.Type == AllAgent.NET.Core.Services.AI.ChunkType.Done);
    }

    [Fact]
    public async Task InteractiveKeyEntryValidatesBeforeSavingAndNeverStoresRejectedKeys()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "commandcode-input-" + Guid.NewGuid());
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("user_good", request.Headers.Authorization!.Parameter);
            return Task.FromResult("""{"data":[{"id":"test/model"}]}""");
        }));
        using var account = new CommandCodeAccount(directory, client);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => account.LoginWithInputAsync("not-a-key", default));
            Assert.Empty(calls); Assert.Null(await account.GetAccessTokenAsync(default));
            await account.LoginWithInputAsync("  user_good  ", default);
            Assert.Equal("user_good", await account.GetAccessTokenAsync(default));
            Assert.Contains("/provider/v1/models", calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ModelCatalogKeepsAdvertisedEffortsAndLimits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "commandcode-models-" + Guid.NewGuid());
        using var client = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("models")
            ? """
                {"schemaVersion":1,"catalogVersion":"test","refreshAfterSeconds":60,"roles":{},
                 "data":[
                   {"id":"deepseek/deepseek-v4-pro","name":"DeepSeek V4 Pro","display":{"label":"DeepSeek V4 Pro (latest)"},"context_length":1000000,"maxOutputTokens":64000,"reasoningEfforts":["off","high","max"]},
                   {"id":"moonshotai/Kimi-K2.7-Code","name":"Kimi K2.7 Code","context_length":262144,"maxOutputTokens":null,"reasoningEfforts":null}
                 ]}
                """
            : "{}")));
        using var account = new CommandCodeAccount(directory, client);
        try
        {
            await account.LoginWithInputAsync("user_test", default);
            var config = account.CreateConfiguration();
            var models = await account.ModelProvider.ListModelsAsync(config, default);
            var deepseek = models.Single(model => model.Id == "deepseek/deepseek-v4-pro");
            Assert.Equal("DeepSeek V4 Pro (latest)", deepseek.DisplayName);
            Assert.Equal(1000000, deepseek.ContextTokens);
            Assert.Equal(64000, deepseek.OutputTokens);
            Assert.Equal(["none", "high", "max"], deepseek.ReasoningLevels);
            var kimi = models.Single(model => model.Id == "moonshotai/Kimi-K2.7-Code");
            Assert.Empty(kimi.ReasoningLevels!);
            config.DefaultModel = deepseek.Id;
            ModelCatalog.Remember(config, models);
            Assert.Equal(["default", "none", "high", "max"], ReasoningSettings.Efforts(config));
            config.ReasoningEffort = "max";
            Assert.Equal("max", CommandCodeProvider.BuildRequest(new ChatRequest(config, [new("user", "hi")]))["params"]!["reasoning_effort"]!.GetValue<string>());
            config.ReasoningEffort = "none";
            Assert.Equal("off", CommandCodeProvider.BuildRequest(new ChatRequest(config, [new("user", "hi")]))["params"]!["reasoning_effort"]!.GetValue<string>());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CreditPayloadParsingKeepsRemainingAmountsAndToleratesMissingFields()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"credits":{"monthlyCredits":120.5,"purchasedCredits":40,"freeCredits":5}}""");
        Assert.True(CommandCodeAccount.TryReadCredits(json.RootElement, out var quota));
        Assert.Equal(165.5, quota.Total); Assert.Contains("剩余", quota.Describe()); Assert.Contains("月付", quota.Describe());
        using var empty = System.Text.Json.JsonDocument.Parse("""{"credits":{}}""");
        Assert.False(CommandCodeAccount.TryReadCredits(empty.RootElement, out _));
        using var missing = System.Text.Json.JsonDocument.Parse("""{"ok":true}""");
        Assert.False(CommandCodeAccount.TryReadCredits(missing.RootElement, out _));
    }

    [Fact]
    public async Task QuotaFooterQueriesOfficialEndpointsAndFormatsPlan()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "commandcode-quota-" + Guid.NewGuid());
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery; calls.Add(path);
            if (path.StartsWith("/provider/v1/models")) return Task.FromResult("""{"data":[{"id":"test/model"}]}""");
            if (path.StartsWith("/alpha/whoami")) return Task.FromResult("""{"org":{"id":"org-1"},"user":{"userName":"tester"}}""");
            if (path.StartsWith("/alpha/billing/credits")) return Task.FromResult("""{"credits":{"monthlyCredits":100,"purchasedCredits":20,"freeCredits":3}}""");
            if (path.StartsWith("/alpha/billing/subscriptions")) return Task.FromResult("""{"data":{"planId":"pro","status":"active","currentPeriodEnd":"2099-11-01T00:00:00Z"}}""");
            return Task.FromResult("{}");
        }));
        using var account = new CommandCodeAccount(directory, client);
        try
        {
            await account.LoginWithInputAsync("user_test", default);
            calls.Clear();
            var items = await account.FetchQuotaAsync(default);
            Assert.Contains(items, item => item.Label == "CommandCode 额度" && item.Value.Contains("剩余 123"));
            Assert.Contains(items, item => item.Label == "CommandCode 计划" && item.Value.StartsWith("pro"));
            Assert.Contains(calls, call => call.StartsWith("/alpha/billing/credits?orgId=org-1"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(await reply(request)) };
    }
}
