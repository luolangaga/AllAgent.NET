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
public sealed class SubAgentIntegrationTests
{
    [Fact]
    public async Task DesktopDelegationUsesConfiguredModelAndReturnsPermissionBlockerWithoutAskingHuman()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent);
        var config = new ApiConfig { ProviderId = "desktop-fixture", DefaultModel = "parent-model" };
        store.SetSetting("subagents:model", "child-model");
        var provider = new DesktopProvider(); var approvals = 0;
        await using var service = new AgentService(store, new AiProviderFactory([provider]), w.Options with
        {
            CodeTimeout = TimeSpan.FromSeconds(60),
            Approver = (_, _) => { Interlocked.Increment(ref approvals); return ValueTask.FromResult(false); }
        });
        var done = new TaskCompletionSource<AgentDone>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<AgentEvent>();
        service.StartStream(parent.Id, "delegate proof", config, (_, evt) =>
        {
            events.Add(evt); if (evt is AgentDone completed) done.TrySetResult(completed); return Task.CompletedTask;
        });
        Assert.Equal("stop", (await done.Task.WaitAsync(TimeSpan.FromSeconds(90))).FinishReason);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Equal(0, approvals); Assert.False(File.Exists(Path.Combine(w.Root, "desktop-child.txt")));
        Assert.DoesNotContain(service.Jobs.List, job => job.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval);
        Assert.Equal("parent-model", config.DefaultModel);
        var child = Assert.Single(store.Conversations, c => c.Id != parent.Id);
        Assert.Equal("child-model", child.ModelId);
        Assert.Contains(store.GetMessages(child.Id), m => m.Role == "tool" && m.Content.Contains("approval denied"));
        Assert.DoesNotContain(store.GetMessages(parent.Id), m => m.Content == "child done");
    }

    [Fact]
    public async Task CancellingDesktopParentAlsoCancelsItsChild()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent);
        store.SetSetting("subagents:model", "child-model");
        var provider = new DesktopProvider(blockChild: true);
        await using var service = new AgentService(store, new AiProviderFactory([provider]), w.Options with { CodeTimeout = TimeSpan.FromSeconds(60) });
        var done = new TaskCompletionSource<AgentDone>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = service.StartStream(parent.Id, "delegate proof", new ApiConfig { ProviderId = provider.ProviderId, DefaultModel = "parent-model" },
            (_, evt) => { if (evt is AgentDone completed) done.TrySetResult(completed); return Task.CompletedTask; });
        await provider.ChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await service.CancelStreamAsync(stream).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("cancelled", (await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).FinishReason);
        await provider.ChildCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class DesktopProvider(bool blockChild = false) : IAiProvider
    {
        public string ProviderId => "desktop-fixture";
        public TaskCompletionSource<bool> ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ChildCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            var child = request.Config.DefaultModel == "child-model";
            if (child && blockChild)
            {
                ChildStarted.TrySetResult(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { if (ct.IsCancellationRequested) ChildCancelled.TrySetResult(true); }
            }
            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                var code = child
                    ? "print(call_tool(\"file_write\", path=\"desktop-child.txt\", content=\"blocked\"))"
                    : "print(call_tool(\"subagent\", agent=\"modify\", task=\"child proof\"))";
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("code", "execute_code", JsonSerializer.Serialize(new { code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
            }
            else
            {
                yield return new(ChunkType.TextDelta, child ? "child done" : "parent done");
                yield return new(ChunkType.Done, FinishReason: "stop");
            }
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    [Fact]
    public async Task ParentAndChildUseRealMicroVmsSeparateHistoriesAndChildWriteStillNeedsApproval()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var jobs = new BackgroundTasks(Path.Combine(w.Root, "jobs"));
        var config = new ApiConfig { ProviderId = "openai", DefaultModel = "fixture", BaseUrl = "http://127.0.0.1" };
        var parent = new Conversation(); var child = new Conversation(); store.AddConversation(parent); store.AddConversation(child);
        var parentProvider = new CodeProvider("print(call_tool(\"subagent\", agent=\"general\", task=\"delegated proof\", background=True))", "parent done");
        var childProvider = new CodeProvider("print(call_tool(\"file_write\", path=\"child.txt\", content=\"blocked\"))", "child done");
        string? jobId = null; var approvals = 0;
        async IAsyncEnumerable<AgentEvent> RunChild(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            var options = w.Options with { IsSubAgent = true, Approver = (request, token) => jobs.ApproveAsync(id, request, token) };
            await using var runtime = await AgentRuntime.CreateAsync(options, ct);
            Assert.DoesNotContain(runtime.Tools.Definitions, d => d.Name == "subagent");
            await foreach (var evt in new AgentEngine(store, childProvider, runtime.CodeAct, options).RunAsync(child.Id, "delegated proof", config, ct)) yield return evt;
        }
        var parentOptions = w.Options with { Approver = (_, _) => { approvals++; return ValueTask.FromResult(true); } };
        await using var parentRuntime = await AgentRuntime.CreateAsync(parentOptions);
        parentRuntime.Tools.Register(new AgentDelegationTools(jobs, (request, token) =>
        {
            jobId = jobs.Start(request.Description, child.Id, "agent", RunChild, parent.Id, ownerToken: request.Background ? default : token);
            if (request.Background) jobs.NotifyParentOnCompletion(jobId);
            return Task.FromResult(new SubAgentLaunch(jobId));
        }));
        var parentEvents = new List<AgentEvent>(); await foreach (var evt in new AgentEngine(store, parentProvider, parentRuntime.CodeAct, parentOptions).RunAsync(parent.Id, "delegate", config)) parentEvents.Add(evt);
        Assert.True(jobId != null, string.Join("\n", parentEvents.Select(evt => JsonSerializer.Serialize(evt, evt.GetType())))); Assert.Equal(0, approvals); Assert.Equal("stop", Assert.IsType<AgentDone>(parentEvents.Last()).FinishReason);
        using var wait = new CancellationTokenSource(30000); while (jobs.Get(jobId!).State != BackgroundTaskState.AwaitingApproval) await Task.Delay(20, wait.Token);
        Assert.False(File.Exists(Path.Combine(w.Root, "child.txt"))); jobs.Respond(jobId!, null); var result = await jobs.WaitAsync(jobId!, wait.Token);
        Assert.Equal(BackgroundTaskState.Completed, result.State); Assert.Equal("child done", result.Output); Assert.False(File.Exists(Path.Combine(w.Root, "child.txt")));
        Assert.DoesNotContain(store.GetMessages(parent.Id), m => m.Content == "child done"); Assert.Contains(store.GetMessages(child.Id), m => m.Role == "tool" && m.Content.Contains("approval denied"));
        Assert.All(parentProvider.Requests.Concat(childProvider.Requests), r => Assert.Equal("execute_code", Assert.Single(r.Tools!).Name));
    }
    [Fact]
    public async Task BackgroundChildCompletionWakesTheIdleDesktopParentWithItsResult()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        store.SetSetting("subagents:model", "child-model");
        var parent = new Conversation(); store.AddConversation(parent);
        var provider = new BackgroundWakeProvider();
        await using var service = new AgentService(store, new AiProviderFactory([provider]), w.Options with { CodeTimeout = TimeSpan.FromSeconds(60) });
        var done = new TaskCompletionSource<AgentDone>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StartStream(parent.Id, "delegate in background", new ApiConfig { ProviderId = provider.ProviderId, DefaultModel = "parent-model" },
            (_, evt) => { if (evt is AgentDone completed) done.TrySetResult(completed); return Task.CompletedTask; });
        Assert.Equal("stop", (await done.Task.WaitAsync(TimeSpan.FromSeconds(90))).FinishReason);
        // The child finishes while the parent is idle; the service wakes the parent and the notification lands in its history.
        await provider.NotificationRead.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Contains(store.GetMessages(parent.Id), message => message.IsInternal && message.Content.Contains("delegated task has ended"));
        Assert.Contains(store.GetMessages(parent.Id), message => message.IsInternal && message.Content.Contains("child report"));
    }

    /// <summary>Parent: spawns a background child, ends its turn, then reads the wake-up notification; child: reports once.</summary>
    private sealed class BackgroundWakeProvider : IAiProvider
    {
        public string ProviderId => "desktop-fixture";
        public TaskCompletionSource<bool> NotificationRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (request.Config.DefaultModel == "child-model")
            {
                yield return new(ChunkType.TextDelta, "child report");
                yield return new(ChunkType.Done, FinishReason: "stop");
                yield break;
            }
            var notification = request.Messages.Any(message => message.Content.Contains("delegated task has ended"));
            if (notification)
            {
                NotificationRead.TrySetResult(true);
                yield return new(ChunkType.TextDelta, "background result acknowledged");
                yield return new(ChunkType.Done, FinishReason: "stop");
                yield break;
            }
            if (!request.Messages.Any(message => message.Role == "tool"))
            {
                var code = "print(call_tool(\"subagent\", agent=\"general\", task=\"background proof\", background=True))";
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("code", "execute_code", JsonSerializer.Serialize(new { code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
                yield break;
            }
            yield return new(ChunkType.TextDelta, "delegation accepted");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private sealed class CodeProvider(string code, string answer) : IAiProvider
    {
        public string ProviderId => "fixture"; public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (Requests.Count == 1) { yield return new(ChunkType.ToolCallComplete, ToolCall: new("code", "execute_code", JsonSerializer.Serialize(new { code }))); yield return new(ChunkType.Done, FinishReason: "tool_calls"); }
            else { yield return new(ChunkType.TextDelta, answer); yield return new(ChunkType.Done, FinishReason: "stop"); }
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }
}
