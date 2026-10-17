using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class AgentDelegationTests
{
    [Fact]
    public async Task ChildScopeCannotDelegateAndSingleModeForwardsSelectors()
    {
        await using var jobs = new BackgroundTasks();
        SubAgentRequest? seen = null;
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            seen = request;
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta("done"); yield return new AgentDone("stop"); }
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run)));
        });
        using (AgentExecutionScope.Enter(true))
        {
            var nested = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "general", task = "nested" }), default);
            Assert.False(nested.Ok);
            Assert.Contains("cannot delegate", nested.Error);
        }
        var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(false); });
        registry.Register(tool);
        var result = await registry.CallAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "explore", task = "research", description = "Find usages", model = "specific", provider = "configured" }), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, approvals);
        Assert.Equal(new SubAgentRequest("explore", "Find usages", "research", "specific", "configured"), seen);
        var schema = tool.Definitions.Single(definition => definition.Name == "subagent").Parameters;
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public async Task PartialFailureReportsFailedIdsAndKeepsSuccessfulOutputs()
    {
        await using var jobs = new BackgroundTasks();
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (request.Prompt == "fail") throw new InvalidOperationException("fixture failure");
                yield return new TokenDelta("saved output");
                yield return new AgentDone("stop");
            }
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run)));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = new[] {
            new { agent = "general", task = "pass" }, new { agent = "general", task = "fail" }
        } }), default);
        Assert.False(result.Ok);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal(1, data.GetProperty("succeeded").GetInt32());
        var failedId = Assert.Single(data.GetProperty("failed").EnumerateArray()).GetString();
        Assert.Equal(data.GetProperty("results")[1].GetProperty("taskId").GetString(), failedId);
        Assert.Contains(failedId!, result.Error!);
        Assert.Contains("saved output", data.GetProperty("results")[0].GetProperty("output").GetString());
        // Failed children keep partial output only; successful children are complete evidence.
        Assert.False(data.GetProperty("results")[0].GetProperty("partial").GetBoolean());
        Assert.True(data.GetProperty("results")[1].GetProperty("partial").GetBoolean());
    }

    [Fact]
    public async Task CancellingParentWaitCancelsItsFrontforegroundChildWithoutNotification()
    {
        await using var jobs = new BackgroundTasks();
        var admitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new AgentDelegationTools(jobs, (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                await release.Task.WaitAsync(ct);
                yield return new AgentDone("stop");
            }
            // Foreground ownership: the parent turn token cancels the child with the turn.
            var id = jobs.Start(request.Description, Guid.NewGuid(), "agent", Run, Guid.NewGuid(), ownerToken: token);
            admitted.SetResult(id);
            return Task.FromResult(new SubAgentLaunch(id));
        });
        using var parent = new CancellationTokenSource();
        var invocation = tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "general", task = "independent" }), parent.Token).AsTask();
        var child = await admitted.Task;
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        await jobs.WaitAsync(child).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Cancelled, jobs.Get(child).State);
        Assert.Null(jobs.Get(child).FinalOutput);
        Assert.False(jobs.Get(child).NotifyParent);
        release.TrySetResult();
    }

    [Fact]
    public async Task BackgroundLaunchReturnsImmediatelyAndSurvivesParentCancellationWithNotification()
    {
        await using var jobs = new BackgroundTasks();
        var requests = new List<SubAgentRequest>();
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            lock (requests) requests.Add(request);
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                yield return new TokenDelta("late result");
                yield return new AgentDone("stop");
            }
            // Background children have no owner token; the host registers the completion notification at launch.
            var id = jobs.Start(request.Description, Guid.NewGuid(), "agent", Run, Guid.NewGuid());
            jobs.NotifyParentOnCompletion(id);
            return Task.FromResult(new SubAgentLaunch(id));
        });
        using var parent = new CancellationTokenSource();
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "general", task = "background work", background = true }), parent.Token);
        Assert.True(result.Ok, result.Error);
        Assert.True(Assert.Single(requests).Background);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal("background", data.GetProperty("mode").GetString());
        var id = data.GetProperty("taskId").GetString()!;
        parent.Cancel();
        var info = await jobs.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Completed, info.State);
        Assert.Equal("late result", info.FinalOutput);
        Assert.True(info.NotifyParent);
        Assert.Single(jobs.PendingNotifications(info.ParentSessionId!.Value));
    }

    [Fact]
    public async Task BackgroundFlagIsRejectedOutsideSingleMode()
    {
        await using var jobs = new BackgroundTasks();
        var tool = new AgentDelegationTools(jobs, (_, _) => throw new InvalidOperationException("must not launch"));
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = new[] { new { agent = "general", task = "x" } }, background = true }), default);
        Assert.False(result.Ok);
        Assert.Contains("single mode only", result.Error);
    }

    [Fact]
    public async Task SteeringARunningChildReturnsWithoutWaiting()
    {
        await using var jobs = new BackgroundTasks();
        var session = Guid.NewGuid(); var parentSession = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            yield return new AgentDone("stop");
        }
        var running = jobs.Start("steerable", session, "agent", Run, parentSession, () => false);
        var tool = new AgentDelegationTools(jobs, (request, _) =>
        {
            var joined = jobs.TryJoin(session, parentSession, _ => { });
            Assert.NotNull(joined);
            return Task.FromResult(new SubAgentLaunch(joined!, Joined: true));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { agent = "general", task = "more work", sessionID = session.ToString() }), default);
        Assert.True(result.Ok, result.Error);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal(running, data.GetProperty("taskId").GetString());
        Assert.Equal("steered", data.GetProperty("results")[0].GetProperty("stopReason").GetString());
        release.SetResult();
        await jobs.WaitAsync(running);
    }

    [Fact]
    public async Task CancelAgentStopsAnOwnedChildAndRejectsUnknownOrForeignTasks()
    {
        await using var jobs = new BackgroundTasks();
        var other = Guid.NewGuid();
        var session = Guid.NewGuid();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); yield return new AgentDone("stop"); }
        var foreign = jobs.Start("foreign", Guid.NewGuid(), "agent", Run, other);
        var tool = new AgentDelegationTools(jobs, (_, _) => throw new InvalidOperationException("must not launch"));
        using (AgentExecutionScope.Enter(false, Guid.NewGuid()))
        {
            var denied = await tool.InvokeAsync("cancel_agent", JsonSerializer.SerializeToElement(new { id = foreign }), default);
            Assert.False(denied.Ok); Assert.Contains("not owned", denied.Error);
        }
        using (AgentExecutionScope.Enter(false, other))
        {
            var cancelled = await tool.InvokeAsync("cancel_agent", JsonSerializer.SerializeToElement(new { id = foreign }), default);
            Assert.True(cancelled.Ok, cancelled.Error);
            Assert.Equal(BackgroundTaskState.Cancelling, jobs.Get(foreign).State);
            var unknown = await tool.InvokeAsync("cancel_agent", JsonSerializer.SerializeToElement(new { id = session.ToString() }), default);
            Assert.False(unknown.Ok);
        }
        jobs.Cancel(foreign);
        await jobs.WaitAsync(foreign);
    }

    [Fact]
    public async Task StuckCancellationReturnsControlWithoutClaimingWorkerStopped()
    {
        await using var jobs = new BackgroundTasks();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            started.SetResult();
            await release.Task;
            yield return new AgentDone("stop");
        }
        var session = Guid.NewGuid();
        var id = jobs.Start("stuck", session, "agent", Run);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => jobs.StopSessionAsync(session));
            Assert.Equal(BackgroundTaskState.Cancelling, jobs.Get(id).State);
            Assert.True(jobs.Active);
        }
        finally { release.SetResult(); await jobs.WaitAsync(id); }
    }

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
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, child.Id, "agent", Run, parent.Id)));
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
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run)));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = Enumerable.Range(0, 8).Select(i => new { agent = "general", task = "Task " + i }).ToArray() }), default);
        Assert.True(result.Ok, result.Error); Assert.True(result.Truncated); Assert.True(result.ToJson().Length <= 60000);
        var results = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(8, results.Length);
        Assert.All(results, item => { Assert.False(string.IsNullOrEmpty(item.GetProperty("taskId").GetString())); Assert.Equal(0, item.GetProperty("exitCode").GetInt32()); Assert.False(item.GetProperty("partial").GetBoolean()); Assert.True(item.GetProperty("outputTruncated").GetBoolean()); });
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
        // Cancelling is not a steering window: admission returns null without throwing.
        Assert.Null(jobs.TryJoin(session, parent, _ => throw new Exception("cannot admit")));
        var stopped = jobs.StopSessionAsync(session);
        Assert.False(stopped.IsCompleted);
        cleanup.SetResult(); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Cancelled, jobs.Get(id).State); Assert.False(jobs.Active);
        Assert.Null(jobs.Get(id).FinalOutput);
        Assert.Null(jobs.TryJoin(session, parent, _ => throw new Exception("cannot admit")));
    }

    [Fact]
    public async Task TimeoutIsReportedAsFailureWithPartialOutput()
    {
        await using var jobs = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TokenDelta("half done");
            yield return new AgentDone("timeout");
        }
        var id = jobs.Start("slow", Guid.NewGuid(), "agent", Run);
        var info = await jobs.WaitAsync(id);
        Assert.Equal(BackgroundTaskState.Failed, info.State);
        Assert.Contains("configured timeout", info.Error);
        Assert.Contains("half done", info.Output);
        Assert.Null(info.FinalOutput);
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
        jobs.Cancel(unrelated); await jobs.WaitAsync(unrelated);
    }

    [Fact]
    public async Task OwnerTokenCancelsQueuedChildBeforeItStarts()
    {
        await using var jobs = new BackgroundTasks(concurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Wait(string id, [EnumeratorCancellation] CancellationToken ct)
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); yield return new AgentDone("stop"); }
        var occupy = jobs.Start("occupy", Guid.NewGuid(), "agent", Wait);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var parent = new CancellationTokenSource(); var childStarted = false;
        async IAsyncEnumerable<AgentEvent> Child(string id, [EnumeratorCancellation] CancellationToken ct)
        { childStarted = true; await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new AgentDone("stop"); }
        var child = jobs.Start("queued", Guid.NewGuid(), "agent", Child, ownerToken: parent.Token);
        parent.Cancel();
        Assert.Equal(BackgroundTaskState.Cancelled, (await jobs.WaitAsync(child)).State);
        Assert.False(childStarted);
        jobs.Cancel(occupy); await jobs.WaitAsync(occupy);
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
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run)));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { chain = new[] {
            new { agent = "explore", task = "first" }, new { agent = "general", task = "use {previous}" }, new { agent = "general", task = "must not start" } } }), default);
        Assert.False(result.Ok); Assert.Equal(new[] { "first", "use findings" }, prompts);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal("chain", data.GetProperty("mode").GetString()); Assert.Equal(2, data.GetProperty("results").GetArrayLength());
        Assert.Equal(1, data.GetProperty("results")[1].GetProperty("exitCode").GetInt32());
        Assert.True(data.GetProperty("results")[1].GetProperty("partial").GetBoolean());
    }

    [Fact]
    public async Task ParallelPreservesInputOrderAndNeverExceedsSixWorkers()
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
            return Task.FromResult(new SubAgentLaunch(jobs.Start(request.Description, Guid.NewGuid(), "agent", Run)));
        });
        var result = await tool.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = Enumerable.Range(0, 8).Select(i => new { agent = "general", task = "task-" + i }).ToArray() }), default);
        Assert.True(result.Ok, result.Error); Assert.Equal(6, maximum);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "task-" + i), data.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("output").GetString()));
    }
}