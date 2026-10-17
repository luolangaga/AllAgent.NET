using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class SubAgentSessionTests
{
    [Fact]
    public async Task AsyncAdmissionIsAwaitedAndCancelledPreparationDoesNotLaunchJob()
    {
        await using var tasks = new BackgroundTasks();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tool = new AgentDelegationTools(tasks, async (request, ct) =>
        {
            preparing.TrySetResult(); await release.Task.WaitAsync(ct);
            return new SubAgentLaunch(tasks.Start(request.Description, Guid.NewGuid(), "agent", Run));
        });
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta("done"); yield return new AgentDone("stop"); }
        var arguments = JsonSerializer.SerializeToElement(new { agent = "general", task = "image task", background = true });
        var pending = tool.InvokeAsync("subagent", arguments, cancellation.Token).AsTask();
        await preparing.Task; Assert.False(pending.IsCompleted); Assert.Empty(tasks.List);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Empty(tasks.List);
        release.SetResult();
        var admitted = await tool.InvokeAsync("subagent", arguments, CancellationToken.None);
        Assert.True(admitted.Ok); var data = JsonSerializer.SerializeToElement(admitted.Data, ToolResult.JsonOptions);
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
    public async Task BackgroundLaunchReturnsRunningSessionAndForegroundContinuationReturnsCompletedAnswer()
    {
        await using var tasks = new BackgroundTasks();
        var session = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubAgentRequest? seen = null;
        var tool = new AgentDelegationTools(tasks, (request, _) =>
        {
            seen = request;
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            { await gate.Task.WaitAsync(ct); yield return new TokenDelta("Findings"); yield return new AgentDone("stop"); }
            return Task.FromResult(new SubAgentLaunch(tasks.Start(request.Description, session, "agent", Run)));
        });
        var registry = new ToolRegistry(); registry.Register(tool);
        var running = await registry.CallAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "explore", description = "Find usages", task = "Search", background = true }), CancellationToken.None);
        Assert.True(running.Ok, running.Error);
        var data = JsonSerializer.SerializeToElement(running.Data, ToolResult.JsonOptions);
        Assert.Equal(session, data.GetProperty("sessionID").GetGuid());
        Assert.Contains(data.GetProperty("status").GetString(), new[] { "Queued", "Running" });
        gate.SetResult(); await tasks.WaitAsync(data.GetProperty("taskId").GetString()!);
        var done = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "general", task = "Explain", sessionID = session.ToString() }), CancellationToken.None);
        Assert.Equal(session, seen!.SessionId);
        var result = JsonSerializer.SerializeToElement(done.Data, ToolResult.JsonOptions);
        Assert.Equal("Completed", result.GetProperty("results")[0].GetProperty("stopReason").GetString());
        Assert.Equal("Findings", result.GetProperty("results")[0].GetProperty("output").GetString());
        Assert.False(result.GetProperty("results")[0].GetProperty("partial").GetBoolean());
    }
}
