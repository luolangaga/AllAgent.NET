using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentDelegationTests
{
    [Fact]
    public async Task ChainCanAdmitAFullPreviousResultBeyondTheLegacyPromptLimit()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent);
        await using var jobs = new BackgroundTasks(); var calls = 0;
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            var child = SubAgentSessions.Resolve(store, jobs, parent.Id, request); store.AddConversation(child);
            var index = ++calls;
            if (index == 2) Assert.True(request.Prompt.Length > 12000);
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta(index == 1 ? new string('x', 16000) : "Verified"); yield return new AgentDone("stop"); }
            return Task.FromResult(jobs.Start(request.Description, child.Id, "agent", Run, parent.Id));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { chain = new[] {
            new { agent = "general", task = "Investigate" }, new { agent = "general", task = "Verify {previous}" }
        } }), default);
        Assert.True(result.Ok, result.Error); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LargeParallelResultsRetainEveryTaskIdentityWithinTheTransportBudget()
    {
        await using var jobs = new BackgroundTasks();
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta(string.Concat(Enumerable.Repeat("<quoted>\"", 2000))); yield return new AgentDone("stop"); }
            return Task.FromResult(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = Enumerable.Range(0, 8).Select(i => new { agent = "general", task = "Task " + i }).ToArray() }), default);
        Assert.True(result.Ok, result.Error); Assert.True(result.Truncated); Assert.True(result.ToJson().Length <= 60000);
        var results = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(8, results.Length);
        Assert.All(results, item => { Assert.False(string.IsNullOrEmpty(item.GetProperty("taskId").GetString())); Assert.Equal(0, item.GetProperty("exitCode").GetInt32()); Assert.True(item.GetProperty("outputTruncated").GetBoolean()); });
    }

    [Fact]
    public async Task ChildCannotAskHumanEvenIfAnEmbeddingHostRegistersTheTool()
    {
        var calls = 0;
        var tool = new AgentInteractionTools((_, _) => { calls++; return Task.FromResult<string?>("must not happen"); });
        using (AgentExecutionScope.Enter(true))
        {
            var result = await tool.AskAsync("Question?", []);
            Assert.False(result.Ok); Assert.Contains("parent", result.Error);
        }
        Assert.Equal(0, calls);
        using var w = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with { IsSubAgent = true, QuestionHandler = (_, _) => throw new Exception("must not ask") });
        Assert.DoesNotContain(runtime.Tools.Definitions, definition => definition.Name == "ask_user");
    }

    [Fact]
    public async Task CancelKeepsWorkerActiveUntilCleanupAndBlocksNewSteering()
    {
        await using var jobs = new BackgroundTasks();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Guid.NewGuid(); var parent = Guid.NewGuid();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { await cleanup.Task; }
            yield return new AgentDone("stop");
        }
        var id = jobs.Start("cancellable", session, "agent", Run, parent, () => false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.Cancel(id);
        Assert.Equal(BackgroundTaskState.Cancelling, jobs.Get(id).State);
        Assert.True(jobs.Active);
        Assert.Throws<InvalidOperationException>(() => jobs.TryJoin(session, parent, _ => throw new Exception("cannot admit")));
        var stopped = jobs.StopSessionAsync(session);
        Assert.False(stopped.IsCompleted);
        cleanup.SetResult(); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Cancelled, jobs.Get(id).State); Assert.False(jobs.Active);
        Assert.Null(jobs.Get(id).FinalOutput);
    }

    [Fact]
    public async Task CancellationStopsOwnedTasksButLeavesOtherParentsAlone()
    {
        await using var jobs = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Wait(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); yield return new AgentDone("stop"); }
        var parent = Guid.NewGuid();
        var one = jobs.Start("one", Guid.NewGuid(), "agent", Wait, parent);
        var two = jobs.Start("two", Guid.NewGuid(), "agent", Wait, parent);
        var unrelated = jobs.Start("other", Guid.NewGuid(), "agent", Wait, Guid.NewGuid());
        await jobs.CancelOwnedAsync(parent).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Cancelled, jobs.Get(one).State);
        Assert.Equal(BackgroundTaskState.Cancelled, jobs.Get(two).State);
        Assert.True(BackgroundTasks.IsActive(jobs.Get(unrelated).State));
    }

    [Fact]
    public async Task OwnerTokenCancelsQueuedChildBeforeItStarts()
    {
        await using var jobs = new BackgroundTasks(concurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Wait(string id, [EnumeratorCancellation] CancellationToken ct)
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); yield return new AgentDone("stop"); }
        jobs.Start("occupy", Guid.NewGuid(), "agent", Wait);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var parent = new CancellationTokenSource(); var childStarted = false;
        async IAsyncEnumerable<AgentEvent> Child(string id, [EnumeratorCancellation] CancellationToken ct)
        { childStarted = true; await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new AgentDone("stop"); }
        var child = jobs.Start("queued", Guid.NewGuid(), "agent", Child, ownerToken: parent.Token);
        parent.Cancel();
        Assert.Equal(BackgroundTaskState.Cancelled, (await jobs.WaitAsync(child)).State);
        Assert.False(childStarted);
    }

    [Fact]
    public async Task ChainPassesPreviousResultAndStopsAtFailure()
    {
        await using var jobs = new BackgroundTasks(); var prompts = new List<string>();
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            prompts.Add(request.Prompt);
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                yield return new TokenDelta(request.Prompt == "first" ? "findings" : "blocked");
                yield return new AgentDone(request.Prompt == "first" ? "stop" : "error");
            }
            return Task.FromResult(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { chain = new[] {
            new { agent = "explore", task = "first" }, new { agent = "general", task = "use {previous}" }, new { agent = "general", task = "must not start" } } }), default);
        Assert.False(result.Ok); Assert.Equal(new[] { "first", "use findings" }, prompts);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal("chain", data.GetProperty("mode").GetString()); Assert.Equal(2, data.GetProperty("results").GetArrayLength());
        Assert.Equal(1, data.GetProperty("results")[1].GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task ParallelPreservesInputOrderAndNeverExceedsFourWorkers()
    {
        await using var jobs = new BackgroundTasks(); var active = 0; var maximum = 0;
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                var count = Interlocked.Increment(ref active);
                lock (jobs) maximum = Math.Max(maximum, count);
                try { await Task.Delay(40, ct); yield return new TokenDelta(request.Prompt); yield return new AgentDone("stop"); }
                finally { Interlocked.Decrement(ref active); }
            }
            return Task.FromResult(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = Enumerable.Range(0, 8).Select(i => new { agent = "general", task = "task-" + i }).ToArray() }), default);
        Assert.True(result.Ok, result.Error); Assert.Equal(4, maximum);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "task-" + i), data.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("output").GetString()));
    }
}
