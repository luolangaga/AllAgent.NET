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
    [Theory]
    [InlineData("text-done", false)]
    [InlineData("part-done", false)]
    [InlineData("item-done", false)]
    [InlineData("completed", false)]
    [InlineData("text-done", true)]
    public async Task ReasoningSnapshotsFillMissingDeltasWithoutDuplicatingStreamedText(string shape, bool streamed)
    {
        var item = new JsonObject { ["type"] = "reasoning", ["id"] = "r1", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "检查配置\n继续分析" }) };
        var events = streamed ? Event(new { type = "response.reasoning_summary_text.delta", output_index = 0, summary_index = 0, delta = "检查配置\n" }) : "";
        events += shape switch
        {
            "text-done" => Event(new { type = "response.reasoning_summary_text.done", output_index = 0, summary_index = 0, text = "检查配置\n继续分析" }),
            "part-done" => Event(new { type = "response.reasoning_summary_part.done", output_index = 0, summary_index = 0, part = new { type = "summary_text", text = "检查配置\n继续分析" } }),
            "item-done" => Event(new { type = "response.output_item.done", output_index = 0, item }), _ => ""
        };
        events += Event(new { type = "response.completed", response = new { status = "completed", output = new JsonArray(item.DeepClone()) } });
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }));
        var thoughts = new StringBuilder();
        await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(new(Config(), [new("user", "fixture")])))
            if (chunk.Type == ChunkType.ThinkingDelta) thoughts.Append(chunk.TextDelta);
        Assert.Equal("检查配置\n继续分析\n", thoughts.ToString());
    }
    [Fact]
    public async Task AccountModelEffortsAreReadFromActualCatalogMetadata()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[{"slug":"new-model","visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"ultra"}]},{"slug":"fixed-model","visibility":"list","supported_reasoning_levels":[]}]}""")
        }));
        var models = await new ResponsesProvider(http).ListModelsAsync(Config());
        Assert.Equal(new[] { "low", "ultra" }, models[0].ReasoningLevels);
        Assert.Empty(models[1].ReasoningLevels!);
        var config = Config(); config.DefaultModel = "new-model"; ModelCatalog.Remember(config, models);
        Assert.Equal(new[] { "default", "low", "ultra" }, ReasoningSettings.Efforts(config));
    }

    private static ApiConfig Config() => new() { ProviderId = "codex", Protocol = "responses", BaseUrl = "https://api.openai.com/v1", DefaultModel = "account-model", ApiKey = "fixture-token", ReasoningEffort = "high", AuthenticationContextId = "account-A" };
    private static string Event(object value) => "data: " + System.Text.Json.JsonSerializer.Serialize(value) + "\n\n";
    [Fact]
    public async Task CacheSessionSurvivesTurnChangesAndSeparatesConversations()
    {
        var sessions = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            sessions.Add(request.Headers.GetValues("session-id").Single());
            return new(HttpStatusCode.OK) { Content = new StringContent(Event(new { type = "response.completed", response = new { status = "completed", output = new object[0] } })) };
        }));
        var provider = new ResponsesProvider(http); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var session in new[] { first, first, second })
            await foreach (var _ in provider.StreamChatAsync(new(Config(), [new("user", "fixture")]) { ConversationId = session, RoutingTurnId = Guid.NewGuid() })) { }
        Assert.Equal(new[] { first.ToString(), first.ToString(), second.ToString() }, sessions);
    }
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

    [Fact]
    public async Task RoutingStateIsStickyOnlyWithinTheSameTurnAndIdentity()
    {
        var headers = new List<string?>(); var counter = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            headers.Add(request.Headers.TryGetValues("x-codex-turn-state", out var values) ? values.Single() : null);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Event(new { type = "response.completed", response = new { status = "completed", output = new object[0] } })) };
            response.Headers.Add("x-codex-turn-state", "route-" + ++counter); return response;
        }));
        var responses = new ResponsesProvider(http); var provider = new RequestRoutingAiProvider(_ => responses); var turn = Guid.NewGuid(); var config = Config();
        async Task Send(ApiConfig cfg, Guid scope) { await foreach (var _ in provider.StreamChatAsync(new(cfg, [new("user", "fixture")]) { RoutingTurnId = scope })) { } }
        await Send(config, turn); await Send(config, turn); await Send(config, turn);
        var other = Config(); other.AuthenticationContextId = "account-B"; await Send(other, turn);
        await Send(config, Guid.NewGuid()); provider.EndRoutingTurn(turn); await Send(config, turn);
        Assert.Equal(new string?[] { null, "route-1", "route-1", null, null, null }, headers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyFinalSnapshotRecoversOrderedNativeOutputAndReplaysItUnchanged(bool includeFinalCall)
    {
        var reason = new JsonObject { ["type"] = "reasoning", ["id"] = "r1", ["summary"] = new JsonArray(), ["encrypted_content"] = "opaque-reason" };
        var call = new JsonObject { ["type"] = "function_call", ["id"] = "fc1", ["call_id"] = "call1", ["name"] = "execute_code", ["namespace"] = "lanagent", ["arguments"] = "{\"code\":\"print(42)\"}", ["status"] = "completed" };
        var events = Event(new { type = "response.output_item.added", output_index = 0, item = new { type = "reasoning", id = "r1", summary = new object[0] } }) +
            Event(new { type = "response.output_item.done", output_index = 0, item = reason }) +
            Event(new { type = "response.output_item.added", output_index = 1, item = new { type = "function_call", id = "fc1", call_id = "call1", name = "execute_code", @namespace = "lanagent", arguments = "" } }) +
            Event(new { type = "response.function_call_arguments.delta", item_id = "fc1", delta = "{\"code\":\"print(42)\"}" }) +
            Event(new { type = "response.output_item.done", output_index = 1, item = call }) +
            Event(new { type = "response.completed", response = new { status = "completed", output = includeFinalCall ? new JsonArray(call.DeepClone()) : new JsonArray() } });
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }));
        var chunks = new List<StreamChunk>(); await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(new(Config(), [new("user", "fixture")]))) chunks.Add(chunk);
        Assert.Single(chunks, c => c.Type == ChunkType.ToolCallComplete);
        var state = Assert.Single(chunks, c => c.Type == ChunkType.ProviderState).ProviderStateJson!;
        var output = JsonNode.Parse(state)!["output"]!.AsArray(); Assert.Equal(2, output.Count);
        Assert.Equal("reasoning", output[0]!["type"]!.GetValue<string>()); Assert.Equal("opaque-reason", output[0]!["encrypted_content"]!.GetValue<string>());
        var historical = new ChatMessage("assistant", "", """[{"id":"call1","type":"function","function":{"name":"execute_code","arguments":"{\"code\":\"print(42)\"}"}}]""", ProviderStateJson: state);
        var normalized = CodeActHistory.Normalize([historical]).Single(); Assert.Equal(state, normalized.ProviderStateJson); Assert.Equal("", normalized.Content);
        var replay = ResponsesProvider.Body(new(Config(), [normalized]))["input"]!.AsArray(); Assert.True(JsonNode.DeepEquals(output, replay));
    }

    [Fact]
    public async Task TextDeltasSurviveEmptyFinalSnapshotWithoutAnItemDoneEvent()
    {
        var events = Event(new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "m1", role = "assistant", content = new object[0] } }) +
            Event(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = "Hello " }) +
            Event(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = "world" }) +
            Event(new { type = "response.completed", response = new { status = "completed", output = new object[0] } });
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }));
        var chunks = new List<StreamChunk>(); await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(new(Config(), [new("user", "fixture")]))) chunks.Add(chunk);
        var state = Assert.Single(chunks, c => c.Type == ChunkType.ProviderState).ProviderStateJson;
        var replay = ResponsesProvider.Body(new(Config(), [new("assistant", "Hello world", ProviderStateJson: state)]));
        Assert.Equal("Hello world", replay["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldEmptyNativeStateFallsBackWithoutWarningsOrLosingText(bool hasCall)
    {
        var calls = hasCall ? """[{"id":"call1","type":"function","function":{"name":"execute_code","arguments":"{\"code\":\"print(42)\"}"}}]""" : null;
        var original = new ChatMessage("assistant", "Keep this answer", calls, ProviderStateJson: """{"protocol":"responses","context_id":"account-A","output":[]}""");
        var normalized = CodeActHistory.Normalize([original]).Single(); Assert.Equal(original.Content, normalized.Content); Assert.Null(normalized.ProviderStateJson);
        foreach (var message in new[] { original, normalized })
        {
            var input = ResponsesProvider.Body(new(Config(), [message]))["input"]!.AsArray();
            Assert.Equal("Keep this answer", input[0]!["content"]!.GetValue<string>());
            Assert.Equal(hasCall ? 2 : 1, input.Count);
        }
    }

    [Fact]
    public void EmptyNativeStateDoesNotBypassForbiddenToolValidation()
    {
        var invalid = new ChatMessage("assistant", "", """[{"id":"call1","function":{"name":"run_shell","arguments":"{}"}}]""", ProviderStateJson: """{"protocol":"responses","output":[]}""");
        var normalized = CodeActHistory.Normalize([invalid]).Single(); Assert.Null(normalized.ToolCallsJson); Assert.Null(normalized.ProviderStateJson); Assert.Contains("incompatible", normalized.Content);
    }

    [Fact]
    public async Task ConfirmedCompletionDoesNotMakeMalformedStreamedArgumentsExecutable()
    {
        var events = Event(new { type = "response.output_item.added", item = new { type = "function_call", id = "fc1", call_id = "call1", name = "execute_code" } }) +
            Event(new { type = "response.function_call_arguments.delta", item_id = "fc1", delta = "{\"code\":" }) +
            Event(new { type = "response.completed", response = new { status = "completed", output = new object[0] } });
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }));
        var chunks = new List<StreamChunk>();
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () => { await foreach (var chunk in new ResponsesProvider(http).StreamChatAsync(new(Config(), [new("user", "fixture")]))) chunks.Add(chunk); });
        Assert.DoesNotContain(chunks, c => c.Type is ChunkType.Done or ChunkType.ToolCallComplete or ChunkType.ProviderState);
    }
}
