using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class SubAgentSessionTests
{
    [Fact]
    public async Task AsyncAdmissionIsAwaitedAndCancelledPreparationDoesNotLaunchJob()
    {
        await using var tasks = new BackgroundTasks();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        async Task<string> Prepare(SubAgentRequest request, CancellationToken ct)
        {
            preparing.TrySetResult(); await release.Task.WaitAsync(ct);
            return tasks.Start(request.Description, Guid.NewGuid(), "agent", Run);
        }
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta("done"); yield return new AgentDone("stop"); }
        var tools = new SubAgentTools(tasks, (_, _, _) => throw new Exception("unexpected legacy launch"), launchAsync: Prepare);
        var pending = tools.DelegateAsync("general", "snapshot", "image task", background: true, ct: cancellation.Token);
        await preparing.Task; Assert.False(pending.IsCompleted); Assert.Empty(tasks.List);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Empty(tasks.List);
        release.SetResult();
        var admitted = await tools.DelegateAsync("general", "snapshot", "image task", background: true);
        Assert.True(admitted.Ok); var data = JsonSerializer.SerializeToElement(admitted.Data);
        Assert.Equal(BackgroundTaskState.Completed, (await tasks.WaitAsync(data.GetProperty("taskId").GetString()!)).State);
        Assert.Single(tasks.List);
    }
    [Fact]
    public async Task ContinuationsRequireParentOwnershipAndIdleChild()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        await using var tasks = new BackgroundTasks();
        var parent = new Conversation(); var other = new Conversation(); store.AddConversation(parent); store.AddConversation(other);
        var child = SubAgentSessions.Resolve(store, tasks, parent.Id, new("explore", "Find callers", "Find references"));
        store.AddConversation(child);
        Assert.Equal(parent.Id, child.ParentConversationId);
        Assert.Equal(child.Id, SubAgentSessions.Resolve(store, tasks, parent.Id, new("general", "Continue", "Implement", SessionId: child.Id)).Id);
        Assert.Throws<ArgumentException>(() => SubAgentSessions.Resolve(store, tasks, other.Id, new("general", "Continue", "Implement", SessionId: child.Id)));
        Assert.Throws<ArgumentException>(() => SubAgentSessions.Resolve(store, tasks, parent.Id, new("build", "Bad role", "Implement")));
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); yield return new AgentDone("stop"); }
        var job = tasks.Start("working", child.Id, "agent", Run);
        Assert.Throws<InvalidOperationException>(() => SubAgentSessions.Resolve(store, tasks, parent.Id, new("general", "Continue", "Implement", SessionId: child.Id)));
        tasks.Cancel(job); await tasks.WaitAsync(job);
        Assert.Equal(child.Id, SubAgentSessions.Resolve(store, tasks, parent.Id, new("general", "Continue", "Implement", SessionId: child.Id)).Id);
    }

    [Fact]
    public async Task TypedForegroundReturnsCompletedAnswerAndBackgroundReturnsRunningSession()
    {
        await using var tasks = new BackgroundTasks();
        var session = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await gate.Task.WaitAsync(ct); yield return new TokenDelta("Findings"); yield return new AgentDone("stop"); }
        SubAgentRequest? seen = null;
        var tools = new SubAgentTools(tasks, (_, _, _) => "unused", request => { seen = request; return tasks.Start(request.Description, session, "agent", Run); });
        var registry = new ToolRegistry(); registry.Register(tools);
        var running = await registry.CallAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "explore", description = "Find usages", prompt = "Search", background = true }), CancellationToken.None);
        Assert.True(running.Ok, running.Error);
        var data = JsonSerializer.SerializeToElement(running.Data);
        Assert.Equal(session, data.GetProperty("sessionID").GetGuid());
        Assert.Contains(data.GetProperty("status").GetString(), new[] { "Queued", "Running" });
        gate.SetResult(); await tasks.WaitAsync(data.GetProperty("taskId").GetString()!);
        var done = await tools.DelegateAsync("general", "Continue", "Explain", sessionID: session.ToString());
        Assert.Equal(session, seen!.SessionId);
        var result = JsonSerializer.SerializeToElement(done.Data);
        Assert.Equal("Completed", result.GetProperty("status").GetString()); Assert.Equal("Findings", result.GetProperty("output").GetString());
    }
}
