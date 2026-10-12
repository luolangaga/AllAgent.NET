using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using Xunit;

namespace LanAgent.Tests;

public sealed class ResponsesProviderTests
{
    private static ApiConfig Config() => new() { ProviderId = "codex", Protocol = "responses", BaseUrl = "https://api.openai.com/v1", DefaultModel = "account-model", ApiKey = "fixture-token", ReasoningEffort = "high", AuthenticationContextId = "account-A" };
    private static string Event(object value) => "data: " + System.Text.Json.JsonSerializer.Serialize(value) + "\n\n";
    [Fact]
    public async Task AccountCatalogPreservesOrderAndOnlyListsVisibleModels()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.AbsoluteUri); Assert.Equal("fixture-token", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"slug":"b","display_name":"Model B","visibility":"list"},{"slug":"hidden","display_name":"Hidden","visibility":"hidden"},{"slug":"a","display_name":"Model A","visibility":"list"}]}""") };
        }));
        var models = await new ResponsesProvider(http).ListModelsAsync(Config()); Assert.Equal(new[] { "b", "a" }, models.Select(m => m.Id)); Assert.Equal("Model B", models[0].DisplayName);
    }
    [Fact]
    public async Task CompletedResponsesExposeOnlyNamespacedCodeActAndPreserveReasoningForSameAccount()
    {
        var output = new JsonArray(new JsonObject { ["type"] = "reasoning", ["id"] = "reason", ["summary"] = new JsonArray(), ["encrypted_content"] = "opaque-account-reasoning" },
            new JsonObject { ["type"] = "function_call", ["id"] = "fc1", ["call_id"] = "call1", ["namespace"] = "lanagent", ["name"] = "execute_code", ["arguments"] = "{\"code\":\"print(42)\"}", ["status"] = "completed" });
        var events = Event(new { type = "response.reasoning_summary_text.delta", delta = "检查工具\n" }) +
            Event(new { type = "response.output_item.added", item = new { type = "function_call", id = "fc1", call_id = "call1", name = "execute_code", @namespace = "lanagent" } }) +
            Event(new { type = "response.function_call_arguments.delta", item_id = "fc1", delta = "{\"code\":\"print(42)\"}" }) +
            Event(new { type = "response.completed", response = new { status = "completed", output, usage = new { input_tokens = 32000, output_tokens = 1200 } } });
        JsonNode? captured = null;
        using var http = new HttpClient(new Handler(request =>
        {
            captured = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }));
        var request = new ChatRequest(Config(), [new("system", "Only execute_code"), new("user", "检查目录")], [new("execute_code", "CodeAct", new { type = "object", properties = new { code = new { type = "string" } } })]);
        var chunks = new List<StreamChunk>(); await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(request)) chunks.Add(chunk);
        Assert.Equal(new ModelTokenUsage(32000, 1200), Assert.Single(chunks, chunk => chunk.Type == ChunkType.Usage).Usage);
        Assert.False(captured!["store"]!.GetValue<bool>()); Assert.True(captured["stream"]!.GetValue<bool>()); Assert.Equal("high", captured["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("Only execute_code", captured["instructions"]!.GetValue<string>()); Assert.Equal("namespace", captured["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("execute_code", captured["tools"]![0]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Null(captured["temperature"]); Assert.Null(captured["max_output_tokens"]); Assert.Null(captured["previous_response_id"]);
        var call = Assert.Single(chunks, c => c.Type == ChunkType.ToolCallComplete); Assert.Equal("call1", call.ToolCall!.Id); Assert.Equal("execute_code", call.ToolCall.Name);
        var state = Assert.Single(chunks, c => c.Type == ChunkType.ProviderState).ProviderStateJson;
        var historical = new ChatMessage("assistant", "", """[{"id":"call1","type":"function","function":{"name":"execute_code","arguments":"{\"code\":\"print(42)\"}"}}]""", ProviderStateJson: state);
        var normalized = CodeActHistory.Normalize(new[] { historical, new("tool", "42", ToolCallId: "call1") }).ToArray(); Assert.Equal(state, normalized[0].ProviderStateJson);
        var sameAccount = ResponsesProvider.Body(request with { Messages = normalized }); Assert.Contains("opaque-account-reasoning", sameAccount.ToJsonString());
        var nextAccount = Config(); nextAccount.AuthenticationContextId = "account-B";
        var changed = ResponsesProvider.Body(request with { Config = nextAccount, Messages = normalized }); Assert.DoesNotContain("opaque-account-reasoning", changed.ToJsonString()); Assert.Contains("function_call_output", changed.ToJsonString());
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task TruncatedAndFailedStreamsNeverExecutePartialCallsOrReportSuccess(string termination)
    {
        var events = Event(new { type = "response.output_item.added", item = new { type = "function_call", id = "fc1", call_id = "call1", name = "execute_code", @namespace = "lanagent" } }) +
            Event(new { type = "response.function_call_arguments.delta", item_id = "fc1", delta = "{\"code\":" });
        if (termination != "missing") events += Event(new { type = "response." + termination, response = new { error = new { code = "subscription_sharing_usage_limit_exceeded" } } });
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }));
        var chunks = new List<StreamChunk>(); await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(new(Config(), [new("user", "fixture")]))) chunks.Add(chunk);
        Assert.Contains(chunks, c => c.Type == ChunkType.Error); Assert.DoesNotContain(chunks, c => c.Type is ChunkType.Done or ChunkType.ToolCallComplete);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
}
