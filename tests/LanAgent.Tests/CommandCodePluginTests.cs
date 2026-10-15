using System.Reflection;
using LanAgent.CommandCode;
using Xunit;

namespace LanAgent.Tests;

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
            var request = new LanAgent.Core.Services.AI.ChatRequest(config, [new("system", "system"), new("user", "hello")], [new("execute_code", "execute", new { type = "object" })]);
            var chunks = new List<LanAgent.Core.Services.AI.StreamChunk>();
            await foreach (var chunk in account.ModelProvider.StreamChatAsync(request)) chunks.Add(chunk);
            Assert.Contains(chunks, c => c.Type == LanAgent.Core.Services.AI.ChunkType.ThinkingDelta);
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
        var chunks = new List<LanAgent.Core.Services.AI.StreamChunk>();
        await foreach (var chunk in CommandCodeProvider.ParseStreamAsync(new StringReader(data))) chunks.Add(chunk);
        Assert.Equal(LanAgent.Core.Services.AI.ChunkType.Error, chunks.Last().Type);
        Assert.DoesNotContain(chunks, c => c.Type == LanAgent.Core.Services.AI.ChunkType.Done);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(await reply(request)) };
    }
}
