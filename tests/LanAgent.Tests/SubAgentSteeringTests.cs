using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class SubAgentSteeringTests
{
    [Fact]
    public async Task AcceptedAfterEngineStopDrainsSameTaskWithRealMicroVmAndOneNotification()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        await using var tasks = new BackgroundTasks();
        var parent = new Conversation(); store.AddConversation(parent);
        var child = SubAgentSessions.Resolve(store, tasks, parent.Id, new("general", "initial", "INITIAL")); store.AddConversation(child);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(); var runs = 0;
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            var index = ++runs;
            await using var runtime = await AgentRuntime.CreateAsync(w.Options with { IsSubAgent = true }, ct);
            var engine = new AgentEngine(store, provider, runtime.CodeAct, w.Options with { IsSubAgent = true });
            await foreach (var evt in engine.RunAsync(child.Id, "INITIAL", new ApiConfig { DefaultModel = "fixture" }, ct, fromInbox: index > 1)) yield return evt;
            if (index == 1) { stopped.SetResult(); await release.Task.WaitAsync(ct); }
        }
        var job = tasks.Start("initial", child.Id, "agent", Run, parent.Id, () => store.GetPendingInputs(child.Id).Any(input => input.Delivery == "steer"));
        tasks.NotifyParentOnCompletion(job);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(tasks.PendingNotifications(parent.Id));
        Assert.Equal(child.Id, SubAgentSessions.Resolve(store, tasks, parent.Id, new("general", "follow-up", "CORRECTION", SessionId: child.Id), allowRunning: true).Id);
        Assert.Throws<ArgumentException>(() => tasks.TryJoin(child.Id, Guid.NewGuid(), _ => throw new Exception("should not run")));
        Assert.Same(child, SubAgentSessions.Resolve(store, tasks, parent.Id, new("explore", "switch", "CORRECTION", SessionId: child.Id), allowRunning: true));
        Assert.Equal("general", child.AgentRole); // Validation does not change an active role before input admission.
        Assert.Equal(job, tasks.TryJoin(child.Id, parent.Id, _ => store.EnqueueInput(child.Id, "CORRECTION", "CORRECTION", [], "steer")));
        release.SetResult(); var result = await tasks.WaitAsync(job).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(BackgroundTaskState.Completed, result.State); Assert.Equal("FINAL-2", result.FinalOutput);
        Assert.Equal(2, runs); Assert.Single(tasks.List); Assert.Single(tasks.PendingNotifications(parent.Id));
        Assert.Empty(store.GetPendingInputs(child.Id));
        Assert.Single(store.GetMessages(child.Id), m => m.Content == "INITIAL"); Assert.Single(store.GetMessages(child.Id), m => m.Content == "CORRECTION");
        Assert.Contains(provider.Requests[1].Messages, m => m.Content == "CORRECTION");
        Assert.All(provider.Requests, request => Assert.Equal("execute_code", Assert.Single(request.Tools!).Name));
        Assert.Null(tasks.TryJoin(child.Id, parent.Id, _ => throw new Exception("must not admit after completion")));
    }

    [Fact]
    public async Task SteeringWhileWaitingForHumanKeepsQuestionAndCancellationRejectsAdmission()
    {
        await using var tasks = new BackgroundTasks(); var session = Guid.NewGuid(); var parent = Guid.NewGuid();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            var response = tasks.AskAsync(id, new AgentQuestion("Choose", ["yes", "no"]), ct);
            waiting.SetResult(); await response;
            yield return new AgentDone("stop");
        }
        var job = tasks.Start("human", session, "agent", Run, parent, () => false);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); var admitted = false;
        Assert.Equal(job, tasks.TryJoin(session, parent, _ => admitted = true)); Assert.True(admitted);
        Assert.Equal(BackgroundTaskState.AwaitingAnswer, tasks.Get(job).State); Assert.NotNull(tasks.Interaction(job));
        tasks.Cancel(job);
        // Either cancellation is in progress (rejected) or the task is already terminal (no join).
        try { Assert.Null(tasks.TryJoin(session, parent, _ => throw new Exception("must not admit"))); }
        catch (InvalidOperationException) { }
        Assert.Equal(BackgroundTaskState.Cancelled, (await tasks.WaitAsync(job)).State);
    }

    private sealed class Provider : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new(ChunkType.TextDelta, TextDelta: "FINAL-" + Requests.Count); yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
