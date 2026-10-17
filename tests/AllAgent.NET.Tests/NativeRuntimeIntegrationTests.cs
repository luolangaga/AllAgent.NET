using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class NativeRuntimeIntegrationTests
{
    [Fact]
    public async Task LargeFileWriteParametersPassThroughFragmentedMicroVmTransport()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider("r = call_tool('file_write', path='large-native.txt', content='x' * 1100000)\nprint(r['ok'])");
        var options = w.Options with { FullAccess = true, CodeTimeout = TimeSpan.FromMinutes(2) };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, options).RunAsync(conversation.Id, "Write a large file", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.True(File.Exists(Path.Combine(w.Root, "large-native.txt")), string.Join("\n", events.OfType<ToolCallCompleted>().Select(e => e.ToolName + ": " + e.ResultJson)));
        Assert.Equal(1100000, new FileInfo(Path.Combine(w.Root, "large-native.txt")).Length);
        Assert.Contains(events, evt => evt is ToolCallCompleted { ToolName: "file_write" } write && write.ResultJson.Contains("\"ok\":true"));
    }

    [Fact]
    public async Task HostToolsExecuteThroughActualMicroVmAndPersistHostTrace()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider("r = call_tool('file_write', path='sample.txt', content='one\\ntwo\\nthree\\n')\nprint(r)\nr = call_tool('file_read', path='sample.txt', offset=2, limit=1)\nprint(r)\nr = call_tool('file_edit', path='sample.txt', edits=[{'old_string':'two', 'new_string':'changed'}])\nprint(r)");
        var options = w.Options with { FullAccess = true };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, options).RunAsync(conversation.Id, "Use host tools", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Equal("one\nchanged\nthree\n", await File.ReadAllTextAsync(Path.Combine(w.Root, "sample.txt")));
        Assert.All(provider.Requests, request => Assert.Equal("execute_code", Assert.Single(request.Tools!).Name));
        Assert.Contains(events, evt => evt is ToolCallCompleted { ToolName: "file_read" } read && read.ResultJson.Contains("two"));
        Assert.Contains(store.GetMessages(conversation.Id), message => message.HostToolTraceJson?.Contains("write") == true);
        Assert.Contains(events, evt => evt is ToolCallCompleted { ToolName: "file_edit" } edit && edit.ResultJson.Contains("\"diff\""));
    }

    [Fact]
    public async Task HostContentAndFilenameSearchAndShellRunThroughCodeAct()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        Directory.CreateDirectory(Path.Combine(w.Root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, ".gitignore"), "ignored.txt\ndata/\n");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "source.txt"), "needle evidence\n");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "source.log"), "needle ignored\n");
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider("print(call_tool('file_search', pattern='needle', path='.', file_pattern='*.txt'))\nprint(call_tool('everything_search', query='*.txt', path='.'))\nprint(call_tool('run_shell', exe='dotnet', args=['--version'], timeout_seconds=30))");
        var options = w.Options with { FullAccess = true, CodeTimeout = TimeSpan.FromMinutes(3) };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, options).RunAsync(conversation.Id, "Native tool checks", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        foreach (var name in new[] { "file_search", "everything_search", "run_shell" })
        {
            var result = Assert.Single(events.OfType<ToolCallCompleted>(), item => item.ToolName == name).ResultJson;
            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), result);
            if (name is "file_search" or "everything_search") { Assert.Contains("source.txt", result); Assert.DoesNotContain("source.log", result); }
        }
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    [Fact]
    public async Task HostFailureIsAValueAndNextHostCallStillRuns()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider("r = call_tool('file_read', path='missing.txt')\nprint(r['ok'])\nr = call_tool('list_directory', path='.')\nprint(r['ok'])");
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "Check failures", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.Contains(events, evt => evt is ToolCallCompleted { ToolName: "file_read" } read && read.ResultJson.Contains("\"ok\":false"));
        Assert.Contains(events, evt => evt is ToolCallCompleted { ToolName: "list_directory" } list && list.ResultJson.Contains("\"ok\":true"));
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    [Fact]
    public async Task RestartRestoresSessionWithoutDuplicatingEarlierUserInput()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var first = new FixtureProvider(null);
        await using (var runtime = await AgentRuntime.CreateAsync(w.Options))
            await foreach (var _ in new AgentEngine(store, first, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "FIRST", new ApiConfig { DefaultModel = "fixture" })) { }
        var second = new FixtureProvider(null);
        await using (var runtime = await AgentRuntime.CreateAsync(w.Options))
            await foreach (var _ in new AgentEngine(store, second, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "SECOND", new ApiConfig { DefaultModel = "fixture" })) { }
        var request = Assert.Single(second.Requests);
        Assert.Single(request.Messages, message => message.Role == "user" && message.Content == "FIRST");
        Assert.Single(request.Messages, message => message.Role == "user" && message.Content == "SECOND");
    }

    [Fact]
    public async Task NativeRuntimeLoadsProjectInstructionsWithoutAnSdkOrCallerCallback()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, "AGENTS.md"), "Project-specific build and verification rule");
        Directory.CreateDirectory(Path.Combine(w.Root, ".allagent"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, ".allagent", "AGENT.md"), "Workspace-specific rule");
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider(null);
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "Use project rules", new ApiConfig { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        var instructions = Assert.Single(provider.Requests).Messages.Single(m => m.Role == "system" && m.Content.Contains("Project-specific"));
        Assert.Contains("Workspace-specific rule", instructions.Content);
        Assert.DoesNotContain("Project-specific", Assert.Single(provider.Requests).Messages[0].Content);
        Assert.Contains(runtime.Tools.Definitions, tool => tool.Name == "file_search");
        Assert.Contains(runtime.Tools.Definitions, tool => tool.Name == "everything_search");
        Assert.DoesNotContain(runtime.Tools.Definitions, tool => tool.Name is "bash" or "powershell");
    }

    [Fact]
    public async Task ManualCompactionPreservesHistoryAndRestoresOnlySummaryAndNewInput()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var original = new Message { ConversationId = conversation.Id, Role = "user", Content = "Original task with important decisions" };
        store.AddMessage(conversation.Id, original);
        store.AddMessage(conversation.Id, new Message { ConversationId = conversation.Id, Role = "assistant", Content = "Completed earlier work" });
        var ids = store.GetMessages(conversation.Id).Select(m => m.Id).ToArray();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var summaries = new FixtureProvider(null);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, summaries, runtime.CodeAct, w.Options).CompactAsync(conversation.Id, new ApiConfig { DefaultModel = "fixture" }, "Keep decisions")) events.Add(evt);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Equal(ids, store.GetMessages(conversation.Id).Select(m => m.Id));
        Assert.Equal(ids[^1], conversation.ContextSummaryThroughId);
        Assert.Equal("Done.", conversation.ContextSummary);
        Assert.Empty(Assert.Single(summaries.Requests).Tools!);
        var resumed = new FixtureProvider(null);
        await foreach (var _ in new AgentEngine(store, resumed, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "New instruction", new ApiConfig { DefaultModel = "fixture" })) { }
        var request = Assert.Single(resumed.Requests);
        Assert.Contains(request.Messages, m => m.Role == "user" && m.Content.Contains("Summary of earlier work") && m.Content.Contains("Done."));
        Assert.DoesNotContain(request.Messages, m => m.Content == original.Content);
        Assert.Single(request.Messages, m => m.Content == "New instruction");
        Assert.Equal(original.Content, store.GetMessages(conversation.Id)[0].Content);
    }

    [Fact]
    public async Task FailedManualCompactionLeavesHistoryAndPreviousSummaryIntact()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation { ContextSummary = "Previous summary" }; store.AddConversation(conversation);
        var message = new Message { ConversationId = conversation.Id, Role = "user", Content = "Keep this history" }; store.AddMessage(conversation.Id, message);
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, new FixtureProvider(null, failAlways: true), runtime.CodeAct, w.Options).CompactAsync(conversation.Id, new ApiConfig { DefaultModel = "fixture" }, "")) events.Add(evt);
        Assert.Single(events.OfType<AgentError>());
        Assert.Equal("error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Equal("Previous summary", conversation.ContextSummary);
        Assert.Null(conversation.ContextSummaryThroughId);
        Assert.Equal(message.Id, Assert.Single(store.GetMessages(conversation.Id)).Id);
    }

    [Fact]
    public async Task CancellationWaitsForTheInFlightHostOperationToCleanUp()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider("print(call_tool('wait_for_cleanup'))");
        var host = new CleanupTool();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        runtime.Tools.Register(host);
        using var cancel = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, w.Options).RunAsync(conversation.Id, "Wait for cleanup", new ApiConfig { DefaultModel = "fixture" }, cancel.Token)) events.Add(evt);
        });
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cancel.Cancel();
            await host.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(run.IsCompleted, "The turn reported completion before its host operation had cleaned up.");
        }
        finally { host.Release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    private sealed class CleanupTool : IAgentToolSet
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = [new("wait_for_cleanup", "Fixture cleanup", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), ToolRisk.Read)];
        public async ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ToolResult.Success(null); }
            finally { Cleaning.TrySetResult(); await Release.Task; }
        }
    }

    [Fact]
    public async Task SuccessfulRetryCompletesTheHostTurnWithStop()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var provider = new FixtureProvider(null, failFirst: true);
        var options = w.Options with { RetryAfterModelOutput = true, ModelRetryDelay = TimeSpan.Zero };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, options).RunAsync(conversation.Id, "Retry the request", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Contains(events, evt => evt is AgentNotice { ResetResponse: true });
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Contains(events, evt => evt is TokenDelta { Text: "Done." });
        Assert.Equal("Done.", Assert.Single(store.GetMessages(conversation.Id), message => message.Role == "assistant").Content);
    }

    [Fact]
    public async Task ExhaustedRetryProducesOneFinalErrorAndEndsTheTurn()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var events = new List<AgentEvent>();
        var options = w.Options with { MaxModelRetries = 0 };
        await foreach (var evt in new AgentEngine(store, new FixtureProvider(null, failAlways: true), runtime.CodeAct, options).RunAsync(conversation.Id, "Error handling", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.Contains("503", Assert.Single(events.OfType<AgentError>()).Message);
        Assert.Equal("error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
    }

    private sealed class FixtureProvider(string? code, bool failFirst = false, bool failAlways = false) : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (failAlways || failFirst && Requests.Count == 1) { yield return new(ChunkType.TextDelta, TextDelta: "Discarded partial response"); yield return new(ChunkType.Error, ErrorMessage: "503 Service Unavailable"); yield break; }
            if (code != null && Requests.Count == 1)
            {
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("code-1", "execute_code", JsonSerializer.Serialize(new { code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
            }
            else { yield return new(ChunkType.TextDelta, TextDelta: "Done."); yield return new(ChunkType.Done, FinishReason: "stop"); }
        }
    }
}
