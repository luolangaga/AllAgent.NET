using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>A main-session run outlives the view that started it: leaving (another session, `/new`,
/// another workspace) keeps the work going and the journal is what a later view replays.</summary>
[Collection("Hyperlight acceptance")]
public sealed class SessionRunsTests
{
    [Fact]
    public async Task RunKeepsWorkingWithoutAViewAndReplaysItsJournal()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var options = w.Options with { Checkpoints = checkpoints };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var runs = new SessionRuns();
        var provider = new Provider();

        var run = runs.Start(store, checkpoints, session.Id, new AgentEngine(store, provider, runtime.CodeAct, options), "write a proof", new ApiConfig { DefaultModel = "fixture" });
        Assert.True(runs.IsRunning(session.Id));
        Assert.Equal(" · 运行中", runs.Label(session.Id));
        // Nobody is attached: the run must finish on its own and be reported afterwards.
        await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(runs.IsRunning(session.Id));
        var finished = Assert.Single(runs.TakeFinished());
        Assert.Equal(session.Id, finished.ConversationId);
        // The journal holds the whole turn, which is what a view attaching late replays.
        var events = run.Journal.Events();
        Assert.Contains(events, item => item.Event is AgentIteration);
        Assert.Contains(events, item => item.Event is AgentDone);
        Assert.Equal(1, events.Count(item => item.Event is AgentDone));
        Assert.Contains("assistant", store.GetVisibleMessages(session.Id).Select(message => message.Role));
        Assert.Empty(checkpoints.List(session.Id).Single().Files);
    }

    [Fact]
    public async Task SecondRunForTheSameConversationIsRejectedAndStopCancelsTheFirst()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var options = w.Options with { Checkpoints = checkpoints };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var runs = new SessionRuns();
        var provider = new Provider { Hang = true };

        var run = runs.Start(store, checkpoints, session.Id, new AgentEngine(store, provider, runtime.CodeAct, options), "long task", new ApiConfig { DefaultModel = "fixture" });
        Assert.Throws<InvalidOperationException>(() =>
            runs.Start(store, checkpoints, session.Id, new AgentEngine(store, provider, runtime.CodeAct, options), "second", new ApiConfig { DefaultModel = "fixture" }));

        runs.RequestStop(run);
        Assert.Equal(SessionRunState.Cancelling, run.State);
        await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("cancelled", run.FinishReason);
        await runs.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.False(runs.Any);
    }

    [Fact]
    public async Task ApprovalRaisedInTheBackgroundWaitsForTheSessionInsteadOfBeingAnswered()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var runs = new SessionRuns();
        // The host gate the terminal uses: with a run in flight it queues the prompt on that run.
        var ui = new TerminalUi(true, false) { Runs = runs, Workspace = w.Root };
        var options = w.Options with
        {
            Checkpoints = checkpoints,
            Permission = PermissionMode.Ask,
            Approver = (request, ct) => ui.RequestApprovalAsync(request, ct)
        };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var provider = new Provider { Code = "r = call_tool('file_write', path='proof.txt', content='ok')\nprint(r['ok'])" };

        var run = runs.Start(store, checkpoints, session.Id, new AgentEngine(store, provider, runtime.CodeAct, options), "write proof", new ApiConfig { DefaultModel = "fixture" });
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (run.State != SessionRunState.AwaitingApproval && DateTime.UtcNow < deadline) await Task.Delay(20);
            // No view is attached, so the prompt waits on the run and the session reads as needing a decision.
            Assert.Equal(SessionRunState.AwaitingApproval, run.State);
            Assert.True(run.Waiting);
            Assert.Equal(1, run.PendingPrompts);
            Assert.Equal(" · 需要授权", runs.Label(session.Id));
            Assert.False(File.Exists(Path.Combine(w.Root, "proof.txt")));

            // Answering it — exactly what an attached view does — releases the run.
            Assert.True(run.TryDequeueApproval(out var waiting));
            waiting!.Completion.TrySetResult(true);
            run.State = SessionRunState.Running;
            await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(File.Exists(Path.Combine(w.Root, "proof.txt")));
            Assert.Equal(0, run.PendingPrompts);
        }
        finally
        {
            // A run still waiting on a prompt must never outlive the test: cancelling it denies the
            // request and lets the sandbox release its execution slot.
            runs.RequestStop(run);
            try { await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { }
        }
    }

    private sealed class Provider : IAiProvider
    {
        public bool Hang { get; init; }
        public string? Code { get; init; }
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }
            if (Code != null && Requests.Count == 1)
            {
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("code-1", "execute_code", System.Text.Json.JsonSerializer.Serialize(new { code = Code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
                yield break;
            }
            yield return new(ChunkType.TextDelta, TextDelta: "Done.");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
