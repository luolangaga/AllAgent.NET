using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class SubAgentToolsTests
{
    [Fact]
    public async Task ForegroundWaitCancellationPreservesChildUntilExplicitTaskCancellation()
    {
        await using var tasks = new BackgroundTasks();
        var admitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new SubAgentTools(tasks, (_, _, _) => "unused", request =>
        {
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                yield return new AgentDone("stop");
            }
            var id = tasks.Start(request.Description, Guid.NewGuid(), "agent", Run);
            admitted.SetResult(id);
            return id;
        });
        using var parent = new CancellationTokenSource();
        var pending = tools.DelegateAsync("general", "child", "work", ct: parent.Token);
        var id = await admitted.Task;
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(BackgroundTasks.IsActive(tasks.Get(id).State));
        Assert.NotEqual(BackgroundTaskState.Cancelling, tasks.Get(id).State);
        Assert.True(tasks.Get(id).NotifyParent);
        await tools.CancelAsync(id);
        Assert.Equal(BackgroundTaskState.Cancelled, (await tasks.WaitAsync(id)).State);
    }

    [Fact]
    public async Task CompletedChildReturnsFinalResponseAndPersistsItSeparatelyFromProgress()
    {
        using var workspace = new TestWorkspace();
        string id;
        await using (var tasks = new BackgroundTasks(workspace.Root))
        {
            async IAsyncEnumerable<AgentEvent> Run(string job, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                yield return new AgentIteration(1, 0, 0);
                yield return new TokenDelta("I will inspect the files.");
                yield return new AgentIteration(2, 0, 1);
                yield return new TokenDelta("Final findings.");
                yield return new AgentDone("stop");
            }
            id = tasks.Start("research", Guid.NewGuid(), "agent", Run);
            var record = await tasks.WaitAsync(id);
            Assert.Contains("inspect", record.Output);
            var tools = new SubAgentTools(tasks, _ => "unused");
            Assert.Equal("Final findings.", Assert.IsType<BackgroundTaskInfo>((await tools.StatusAsync(id)).Data).Output);
        }
        await using var reopened = new BackgroundTasks(workspace.Root);
        Assert.Equal("Final findings.", reopened.Get(id).FinalOutput);
    }

    [Fact]
    public async Task StreamWithoutCompletionCannotReportSuccess()
    {
        await using var tasks = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Run(string job, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new TokenDelta("Partial answer");
            yield return new AgentError("connection lost");
        }
        var id = tasks.Start("broken stream", Guid.NewGuid(), "agent", Run);
        var record = await tasks.WaitAsync(id);
        Assert.Equal(BackgroundTaskState.Failed, record.State);
        Assert.Equal("connection lost", record.Error);
        Assert.Null(record.FinalOutput);
    }

    [Fact]
    public void ModelSelectionInheritsAnIndependentCopyAndRetainsAuthentication()
    {
        var parent = new ApiConfig { ProviderId = "openai", DefaultModel = "current", AuthenticationContextId = "auth", Models = ["current"], ModelContextWindows = new() { ["current"] = 32000 } };
        var child = SubAgentModelSelection.Resolve(parent, [parent]);
        Assert.Equal(parent.DefaultModel, child.DefaultModel);
        Assert.Equal("auth", child.AuthenticationContextId);
        child.Models.Add("extra"); child.ModelContextWindows["current"] = 8000;
        Assert.Single(parent.Models); Assert.Equal(32000, parent.ModelContextWindows["current"]);
        var overridden = SubAgentModelSelection.Resolve(parent, [parent], "other");
        Assert.Equal("other", overridden.DefaultModel); Assert.Equal("current", parent.DefaultModel);
    }

    [Fact]
    public void ModelSelectionUsesConfiguredProviderAndRejectsDisabledOrAmbiguousSelectors()
    {
        var parent = new ApiConfig { ProviderId = "openai", DefaultModel = "current" };
        var other = new ApiConfig { ProviderId = "anthropic", DisplayName = "Research", DefaultModel = "research", ApiKey = "other-key" };
        var selected = SubAgentModelSelection.Resolve(parent, [parent, other], provider: "Research");
        Assert.Equal("research", selected.DefaultModel); Assert.Equal("other-key", selected.ApiKey);
        Assert.Equal(other.Id, SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString()[..8]).Id);
        Assert.Equal(other.Id, SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString("N")).Id);
        Assert.Equal("current", parent.DefaultModel);
        Assert.Throws<ArgumentException>(() => SubAgentModelSelection.Resolve(parent, [other, new ApiConfig { ProviderId = "anthropic" }], provider: "anthropic"));
        other.IsEnabled = false;
        Assert.Throws<ArgumentException>(() => SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString()));
    }

    [Fact]
    public async Task TypedDelegationForwardsModelOverridesWithoutAnExtraApproval()
    {
        using var workspace = new TestWorkspace(); await using var tasks = new BackgroundTasks(Path.Combine(workspace.Root, "jobs"));
        string? seenTask = null, seenModel = null, seenProvider = null;
        var tools = new SubAgentTools(tasks, (task, model, provider) => { seenTask = task; seenModel = model; seenProvider = provider; return "fixture-id"; });
        var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(false); }); registry.Register(tools);
        var result = await registry.CallAsync("start_agent", JsonSerializer.SerializeToElement(new { task = "research", model = "specific", provider = "configured" }), CancellationToken.None);
        Assert.True(result.Ok, result.Error); Assert.Equal(0, approvals);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal("fixture-id", data.GetProperty("taskId").GetString());
        Assert.False(data.TryGetProperty("id", out _));
        Assert.Equal("research", seenTask); Assert.Equal("specific", seenModel); Assert.Equal("configured", seenProvider);
        Assert.DoesNotContain("model", tools.Definitions.Single(d => d.Name == "start_agent").Parameters.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task ChildScopeAndInvalidTasksCannotLaunchAgents()
    {
        using var workspace = new TestWorkspace(); await using var tasks = new BackgroundTasks(Path.Combine(workspace.Root, "jobs"));
        var launches = 0; var tools = new SubAgentTools(tasks, _ => { launches++; return "id"; });
        Assert.False((await tools.StartAsync(" ")).Ok);
        Assert.False((await tools.StartAsync(new string('x', 12001))).Ok);
        using (AgentExecutionScope.Enter(true)) Assert.False((await tools.StartAsync("nested")).Ok);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task WaitReturnsChildResultAndSurfacesHumanApprovalWithoutHanging()
    {
        using var workspace = new TestWorkspace(); await using var tasks = new BackgroundTasks(Path.Combine(workspace.Root, "jobs"));
        var tools = new SubAgentTools(tasks, _ => "unused");
        async IAsyncEnumerable<AgentEvent> Complete(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(20, ct); yield return new TokenDelta("proof"); yield return new AgentDone("stop"); }
        var completedId = tasks.Start("test", Guid.NewGuid(), "agent", Complete);
        var result = await tools.WaitAsync(completedId, 5);
        var info = Assert.IsType<BackgroundTaskInfo>(result.Data); Assert.Equal(BackgroundTaskState.Completed, info.State); Assert.Equal("proof", info.Output);
        async IAsyncEnumerable<AgentEvent> Approval(string id, [EnumeratorCancellation] CancellationToken ct)
        { await tasks.ApproveAsync(id, new ToolApprovalRequest("file_write", ToolRisk.Write, JsonSerializer.SerializeToElement(new { path = "proof.txt" })), ct); yield return new AgentDone("stop"); }
        var pendingId = tasks.Start("approval", Guid.NewGuid(), "agent", Approval);
        var pending = Assert.IsType<BackgroundTaskInfo>((await tools.WaitAsync(pendingId, 5)).Data);
        Assert.Equal(BackgroundTaskState.AwaitingApproval, pending.State);
        Assert.False((await tools.WaitAsync(pendingId, 31)).Ok);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.WaitAsync(pendingId, 0, cancelled.Token));
        await tools.CancelAsync(pendingId);
        Assert.Equal(BackgroundTaskState.Cancelled, (await tasks.WaitAsync(pendingId)).State);
    }
}
