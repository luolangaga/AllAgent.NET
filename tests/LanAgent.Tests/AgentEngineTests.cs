using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentEngineTests
{
    [Fact]
    public async Task UnavailableSandboxStopsBeforeAnyModelRequest()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var provider = new ScriptedProvider(Answer("must not be contacted"));
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox { FailPreparation = true });
        var events = await Collect(new AgentEngine(store, provider, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Empty(provider.Requests);
        Assert.Contains(events, e => e is AgentError { Message: "MicroVM unavailable" });
        Assert.Equal("error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task ActualProviderRequestsHideLegacyDirectCallsAndPreserveValidNativeState(string protocol)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        void Add(string id, string name, string args, string? state)
        {
            store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", Content = "previous operation", ToolCallsJson = JsonSerializer.Serialize(new[] { new { id, type = "function", function = new { name, arguments = args } } }), ProviderStateJson = state });
            store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "tool", ToolCallId = id, ToolName = name, Content = "{\"ok\":true,\"data\":{}}" });
        }
        string State(string name, string id) => protocol switch
        {
            "anthropic" => JsonSerializer.Serialize(new { protocol, content = new object[] { new { type = "thinking", thinking = "thought", signature = "untouched-signature" }, new { type = "tool_use", id, name, input = new { code = "print(1)" } } } }),
            "gemini" => JsonSerializer.Serialize(new { protocol, parts = new object[] { new { functionCall = new { name, args = new { code = "print(1)" } }, thoughtSignature = "untouched-signature" } } }),
            _ => "{}"
        };
        Add("legacy", "list_directory", "{}", State("list_directory", "legacy"));
        Add("valid", "execute_code", "{\"code\":\"print(1)\"}", protocol == "openai" ? null : State("execute_code", "valid"));
        Add("malformed", "execute_code", "{bad", null);
        JsonElement body = default;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body); body = request.RootElement.Clone();
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(protocol switch
            {
                "anthropic" => "event: content_block_start\ndata: {\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_block_delta\ndata: {\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}\n\nevent: message_stop\ndata: {}\n\n",
                "gemini" => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            });
        });
        var config = new ApiConfig { ProviderId = protocol, BaseUrl = server.Url, DefaultModel = "fixture", ApiKey = "fixture" };
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), new FakeSandbox());
        var events = await Collect(new AgentEngine(store, new AiProviderFactory().GetProvider(config), codeAct, workspace.Options).RunAsync(session.Id, "continue", config));
        Assert.DoesNotContain(events, e => e is AgentError);
        var definitions = protocol == "gemini" ? body.GetProperty("tools")[0].GetProperty("functionDeclarations") : body.GetProperty("tools");
        var definition = Assert.Single(definitions.EnumerateArray());
        Assert.Equal("execute_code", protocol == "openai" ? definition.GetProperty("function").GetProperty("name").GetString() : definition.GetProperty("name").GetString());
        var messages = protocol == "gemini" ? body.GetProperty("contents") : body.GetProperty("messages");
        foreach (var message in messages.EnumerateArray()) CheckFunctions(message);
        if (protocol != "openai") Assert.Contains("untouched-signature", messages.GetRawText());
        Assert.DoesNotContain("{bad", messages.GetRawText());
        var instructions = protocol switch
        {
            "anthropic" => body.GetProperty("system")[0].GetProperty("text").GetString(),
            "gemini" => body.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString(),
            _ => body.GetProperty("messages")[0].GetProperty("content").GetString()
        };
        Assert.Contains("Use path=\".\" for the workspace root", instructions);
        if (protocol == "anthropic")
        {
            Assert.Equal(2, body.GetProperty("system").GetArrayLength());
            Assert.Contains("helpful agent", body.GetProperty("system")[1].GetProperty("text").GetString());
            Assert.Equal("ephemeral", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        }
        else if (protocol == "gemini") Assert.Equal(2, body.GetProperty("system_instruction").GetProperty("parts").GetArrayLength());
        else Assert.Equal("system", body.GetProperty("messages")[1].GetProperty("role").GetString());
        Assert.Contains(store.GetMessages(session.Id), m => m.ToolCallsJson?.Contains("list_directory") == true); // Audit remains intact.
    }

    private static void CheckFunctions(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckFunctions(child);
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("type", out var type) && type.GetString() == "tool_use") Assert.Equal("execute_code", element.GetProperty("name").GetString());
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "function" or "functionCall" or "functionResponse") Assert.Equal("execute_code", property.Value.GetProperty("name").GetString());
            CheckFunctions(property.Value);
        }
    }

    [Fact]
    public async Task DirectCallsAreNotExecutedAndRepeatedViolationsStopAfterOneCorrection()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var sandbox = new FakeSandbox();
        StreamChunk[] Direct(string id) => [new(ChunkType.ToolCallComplete, ToolCall: new(id, "list_directory", "{}"))];
        var provider = new ScriptedProvider(Direct("first"), Direct("second"));
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), sandbox);
        var events = await Collect(new AgentEngine(store, provider, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.False(sandbox.Started.Task.IsCompleted);
        Assert.Empty(events.OfType<ToolCallStarted>());
        Assert.Equal(2, events.OfType<ToolCallRejected>().Count());
        Assert.Equal("protocol_error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.DoesNotContain(provider.Requests[1].Messages, m => m.ToolCallsJson?.Contains("list_directory") == true || m.ToolName == "list_directory");
        Assert.Contains(provider.Requests[1].Messages, m => m.Content.Contains("Not executed"));
    }
    private static ApiConfig Config => new() { ProviderId = "openai", DefaultModel = "test-model", BaseUrl = "http://127.0.0.1" };

    [Fact]
    public async Task ProjectAndSessionChangesKeepTheFirstSystemCacheBlockIdentical()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation { SystemPrompt = "Project A policy" }; store.AddConversation(session);
        var sessionPolicy = "Session A policy";
        var model = new ScriptedProvider(Answer("first"), Answer("second"));
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox());
        var engine = new AgentEngine(store, model, codeAct, workspace.Options with { InstructionsProvider = () => sessionPolicy });
        await Collect(engine.RunAsync(session.Id, "task", Config));
        session.SystemPrompt = "Project B policy"; store.UpdateConversation(session); sessionPolicy = "Session B policy";
        await Collect(engine.RunAsync(session.Id, "continue", Config));
        var first = model.Requests[0].Messages.Where(message => message.Role == "system").ToArray();
        var second = model.Requests[1].Messages.Where(message => message.Role == "system").ToArray();
        Assert.Equal(2, first.Length); Assert.Equal(2, second.Length);
        Assert.Equal(first[0], second[0]); Assert.Equal(codeAct.Instructions, first[0].Content);
        Assert.Contains("Project A policy", first[1].Content); Assert.Contains("Session A policy", first[1].Content);
        Assert.Contains("Project B policy", second[1].Content); Assert.Contains("Session B policy", second[1].Content);
        var firstBody = AnthropicProvider.Body(model.Requests[0]); var secondBody = AnthropicProvider.Body(model.Requests[1]);
        Assert.Equal(firstBody["system"]![0]!.ToJsonString(), secondBody["system"]![0]!.ToJsonString());
        Assert.Equal("ephemeral", firstBody["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(3, firstBody.ToJsonString().Split("cache_control").Length - 1);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("max_tokens")]
    [InlineData("MAX_TOKENS")]
    [InlineData("max_output_tokens")]
    public async Task TruncationContinuesWithoutExecutingEvenValidPythonPrefixes(string reason)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var sandbox = new FakeSandbox();
        var truncated = Call("discarded").Append(new StreamChunk(ChunkType.Done, FinishReason: reason)).ToArray();
        var model = new ScriptedProvider(truncated, Call("complete"), Answer("Finished"));
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), sandbox);
        var events = await Collect(new AgentEngine(store, model, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Contains(events, e => e is AgentNotice);
        Assert.Single(sandbox.Results);
        Assert.DoesNotContain(events.OfType<ToolCallStarted>(), e => e.Id == "discarded");
        Assert.DoesNotContain(store.GetMessages(session.Id), m => m.ToolCallsJson?.Contains("discarded") == true);
        Assert.Contains(model.Requests[1].Messages, m => m.Content.Contains("NOT executed"));
        Assert.Contains(store.GetMessages(session.Id), m => m.IsInternal && m.Content.Contains("NOT executed"));
    }

    [Fact]
    public async Task TransientModelFailureRetriesWithoutReplayingCompletedHostCalls()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var registry = new ToolRegistry(); registry.Register(workspace.Tools); var sandbox = new FakeSandbox();
        var model = new ScriptedProvider(Call("first"), [new(ChunkType.Usage) { Usage = new(50, 5) }, new(ChunkType.Error, ErrorMessage: "HTTP 503: service unavailable")], Answer("done"));
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), sandbox);
        var events = await Collect(new AgentEngine(store, model, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Equal(3, model.Requests.Count); Assert.Single(sandbox.Results);
        Assert.Contains(events, e => e is AgentNotice notice && notice.Message.Contains("重试"));
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Equal(new ModelUsageStats("openai", "test-model", 50, 5, 3, 1), Assert.Single(store.GetTokenUsageStats()));
    }

    [Fact]
    public async Task EmptyCompletionIsExplicitFailureInsteadOfSilentSuccess()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox());
        var events = await Collect(new AgentEngine(store, new ScriptedProvider(new StreamChunk[] { new(ChunkType.Done, FinishReason: "stop") }), codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Contains(events, e => e is AgentError error && error.Message.Contains("未返回回答"));
        Assert.Equal("error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    [Fact]
    public async Task AutomaticSummaryKeepsFullHistoryAndIsReusedOnResume()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        for (var index = 0; index < 12; index++) store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = index % 2 == 0 ? "user" : "assistant", Content = index + new string('x', 1200) });
        var model = new ScriptedProvider(
            Answer("preserved goal, completed files, next action").Prepend(new(ChunkType.Usage) { Usage = new(10, 2) }).ToArray(),
            Answer("done").Prepend(new(ChunkType.Usage) { Usage = new(20, 3) }).ToArray(),
            Answer("resumed").Prepend(new(ChunkType.Usage) { Usage = new(30, 4) }).ToArray());
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox());
        session.SystemPrompt = "Project compaction policy"; store.UpdateConversation(session);
        var engine = new AgentEngine(store, model, codeAct, workspace.Options with { MaxContextCharacters = codeAct.Instructions.Length + 12000, InstructionsProvider = () => "Session compaction policy" });
        var events = await Collect(engine.RunAsync(session.Id, "task", Config));
        Assert.True(!events.OfType<AgentError>().Any(), string.Join("; ", events.OfType<AgentError>().Select(error => error.Message)));
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Empty(model.Requests[0].Tools!); Assert.Contains(model.Requests[1].Messages, m => m.Content.Contains("preserved goal"));
        Assert.DoesNotContain("Project compaction policy", model.Requests[0].Messages.Last().Content);
        var systems = model.Requests[1].Messages.Where(message => message.Role == "system").ToArray();
        Assert.Equal(2, systems.Length); Assert.Equal(codeAct.Instructions, systems[0].Content);
        Assert.Contains("Project compaction policy", systems[1].Content); Assert.Contains("Session compaction policy", systems[1].Content);
        Assert.NotNull(session.ContextSummaryThroughId); Assert.Equal(14, store.GetMessages(session.Id).Count);
        await Collect(engine.RunAsync(session.Id, "resume", Config));
        Assert.Contains(model.Requests.Last().Messages, m => m.Content.Contains("preserved goal"));
        Assert.DoesNotContain(model.Requests.Last().Messages, m => m.Content.StartsWith("0xxx"));
        Assert.Equal(systems, model.Requests.Last().Messages.Where(message => message.Role == "system").ToArray());
        Assert.Equal(new ModelUsageStats("openai", "test-model", 60, 9, 3, 3), Assert.Single(store.GetTokenUsageStats()));
    }

    [Fact]
    public async Task ActualUsageUsesSelectedModelCapacityAndPersistsForIdleDisplay()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session); var config = Config; config.ModelContextWindows[config.DefaultModel] = 400000;
        var model = new ScriptedProvider(new StreamChunk[] { new(ChunkType.TextDelta, "done"), new(ChunkType.Usage) { Usage = new(32000, 1200) }, new(ChunkType.Done, FinishReason: "stop") });
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox());
        var events = await Collect(new AgentEngine(store, model, codeAct, workspace.Options).RunAsync(session.Id, "task", config));
        Assert.Contains(events, e => e is AgentContextUsage { UsedTokens: 33200, CapacityTokens: 400000, Estimated: false });
        Assert.Equal(33200, store.GetConversation(session.Id)!.ContextUsedTokens); Assert.False(session.ContextUsageEstimated);
        Assert.Equal(new ModelUsageStats("openai", "test-model", 32000, 1200, 1, 1), Assert.Single(store.GetTokenUsageStats()));
    }

    [Fact]
    public async Task DisconnectedOpenAiStreamDoesNotCommitValidLookingCode()
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"partial\",\"function\":{\"name\":\"execute_code\",\"arguments\":\"{\\\"code\\\":\\\"print(1)\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n");
        });
        var config = Config; config.BaseUrl = server.Url; var chunks = new List<StreamChunk>();
        await foreach (var chunk in new OpenAiProvider().StreamChatAsync(new(config, [new("user", "task")]))) chunks.Add(chunk);
        Assert.Contains(chunks, chunk => chunk.Type == ChunkType.Error);
        Assert.DoesNotContain(chunks, chunk => chunk.Type is ChunkType.ToolCallComplete or ChunkType.Done);
    }

    [Fact]
    public async Task FullLoopPersistsPairedCallsAndOnlyExposesExecuteCode()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var provider = new ScriptedProvider(Call("tc1"), Answer("Finished"));
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), new FakeSandbox());
        var engine = new AgentEngine(store, provider, codeAct, workspace.Options);
        var events = await Collect(engine.RunAsync(session.Id, "List files", Config));
        Assert.Contains(events, e => e is TokenDelta { Text: "Finished" });
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal("execute_code", Assert.Single(request.Tools!).Name));
        Assert.Single(provider.Requests[0].Messages, m => m.Role == "user");
        var saved = store.GetMessages(session.Id);
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant" }, saved.Select(m => m.Role));
        Assert.Equal("tc1", saved[2].ToolCallId);
    }

    [Fact]
    public async Task OldInterruptedHistoryIsRepairedBeforeResuming()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", ToolCallsJson = """[{"id":"old","type":"function","function":{"name":"execute_code","arguments":"{}"}}]""" });
        var provider = new ScriptedProvider(Answer("Recovered"));
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox());
        await Collect(new AgentEngine(store, provider, codeAct, workspace.Options).RunAsync(session.Id, "continue", Config));
        Assert.Contains(provider.Requests[0].Messages, m => m.Role == "tool" && m.ToolCallId == "old");
    }

    [Fact]
    public async Task CancellationPersistsToolFailureAndReportsCancelled()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var provider = new ScriptedProvider(Call("cancelled-call"));
        var sandbox = new FakeSandbox { Block = true };
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), sandbox);
        using var cancel = new CancellationTokenSource();
        var collection = Collect(new AgentEngine(store, provider, codeAct, workspace.Options).RunAsync(session.Id, "task", Config, cancel.Token));
        await sandbox.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        var events = await collection;
        Assert.Equal("cancelled", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Contains(store.GetMessages(session.Id), m => m.Role == "tool" && m.ToolCallId == "cancelled-call" && m.Content.Contains("interrupted"));
    }

    [Fact]
    public async Task HostToolBudgetStopsFurtherOperations()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        var sandbox = new FakeSandbox { Calls = 3 };
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), sandbox);
        var events = await Collect(new AgentEngine(store, new ScriptedProvider(Call("budget")), codeAct, workspace.Options with { MaxToolCalls = 1 }).RunAsync(session.Id, "task", Config));
        Assert.Equal("tool_limit", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Equal(1, sandbox.Results.Count(r => r.Ok));
        Assert.Equal(2, sandbox.Results.Count(r => !r.Ok));
    }

    [Fact]
    public async Task MalformedArgumentsDoNotExecuteSandbox()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var sandbox = new FakeSandbox();
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), sandbox);
        var provider = new ScriptedProvider([new(ChunkType.ToolCallComplete, ToolCall: new("invalid", "execute_code", "{bad json"))], Answer("Cannot execute"));
        var events = await Collect(new AgentEngine(store, provider, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.False(sandbox.Started.Task.IsCompleted);
        Assert.Contains(events.OfType<ToolCallCompleted>(), e => e.ResultJson.Contains("Malformed"));
    }

    [Fact]
    public async Task ErrorsProduceTerminalStatusAndLimitsAreExplicit()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new FakeSandbox { Calls = 0 });
        var error = await Collect(new AgentEngine(store, new ScriptedProvider(new StreamChunk[] { new(ChunkType.Error, ErrorMessage: "upstream failed") }), codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Contains(error, e => e is AgentError { Message: "upstream failed" });
        Assert.Equal("error", Assert.IsType<AgentDone>(error.Last()).FinishReason);
        var steps = Enumerable.Range(0, 110).Select(index => Call("step-" + index)).Append(Answer("done")).ToArray();
        var longRun = await Collect(new AgentEngine(store, new ScriptedProvider(steps), codeAct, workspace.Options).RunAsync(session.Id, "task2", Config));
        Assert.Equal(110, longRun.OfType<ToolCallStarted>().Count(call => call.ToolName == "execute_code"));
        Assert.Equal("stop", Assert.IsType<AgentDone>(longRun.Last()).FinishReason);
    }

    [Theory]
    [InlineData("The SSL connection could not be established, see inner exception.")]
    [InlineData("TLS handshake failed: connection reset")]
    public async Task RuntimeSslErrorsRetryCurrentRequestAndDiscardPartialResponse(string error)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var registry = new ToolRegistry(); registry.Register(workspace.Tools); var sandbox = new FakeSandbox();
        var model = new ScriptedProvider(Call("completed-first"),
            [new(ChunkType.TextDelta, TextDelta: "DISCARDED-PARTIAL"), new(ChunkType.ToolCallDelta, ToolCall: new("discarded-call", "execute_code", "{")), new(ChunkType.Error, ErrorMessage: error)],
            Answer("SSL-RECOVERED"));
        await using var codeAct = new CodeActProvider(registry, new(workspace.Python), sandbox);
        var events = await Collect(new AgentEngine(store, model, codeAct, workspace.Options).RunAsync(session.Id, "task", Config));
        Assert.Equal(3, model.Requests.Count); Assert.Single(sandbox.Results);
        Assert.Contains(events, e => e is AgentNotice { ResetResponse: true });
        Assert.DoesNotContain(events.OfType<ToolCallStarted>(), e => e.Id == "discarded-call");
        Assert.DoesNotContain(store.GetMessages(session.Id), m => m.Content.Contains("DISCARDED-PARTIAL"));
        Assert.Equal("SSL-RECOVERED", store.GetMessages(session.Id).Last(m => m.Role == "assistant").Content);
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    private static StreamChunk[] Call(string id) => [new(ChunkType.ToolCallDelta, ToolCall: new(id, "execute_code", "{\"code\":")), new(ChunkType.ToolCallDelta, ToolCall: new(id, "", "\"print(1)\"}")), new(ChunkType.ToolCallComplete, ToolCall: new(id, "execute_code", "{\"code\":\"print(1)\"}"))];
    private static StreamChunk[] Answer(string text) => [new(ChunkType.TextDelta, TextDelta: text), new(ChunkType.Done, FinishReason: "stop")];
    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> stream) { var list = new List<AgentEvent>(); await foreach (var evt in stream) list.Add(evt); return list; }

    private sealed class FakeSandbox : ICodeSandbox
    {
        public bool Block; public bool FailPreparation; public int Calls = 1; public List<ToolResult> Results = [];
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) => FailPreparation
            ? throw new InvalidOperationException("MicroVM unavailable") : Task.FromResult<CodeSandboxContext?>(null);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, ct);
            for (var index = 0; index < Calls; index++) Results.Add(await callback("list_directory", JsonSerializer.SerializeToElement(new { path = "." }), ct));
            return ToolResult.Success(new { stdout = "1\n", stderr = "", exitCode = 0 });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ScriptedProvider(params StreamChunk[][] responses) : IAiProvider
    {
        public string ProviderId => "openai";
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (var chunk in responses[Requests.Count - 1]) { ct.ThrowIfCancellationRequested(); yield return chunk; }
            if (!responses[Requests.Count - 1].Any(chunk => chunk.Type is ChunkType.Done or ChunkType.Error)) yield return new(ChunkType.Done, FinishReason: "tool_calls");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }
}
