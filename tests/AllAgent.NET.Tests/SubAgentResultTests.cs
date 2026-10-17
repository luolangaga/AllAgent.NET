using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Child result contract: final answers, partial output and notification payloads.</summary>
public sealed class SubAgentResultTests
{
    [Fact]
    public async Task CompletedChildFinalResponsePersistsSeparatelyFromProgress()
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
            Assert.Equal("Final findings.", record.FinalOutput);
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
        Assert.Equal("Partial answer", record.Output);
    }

    [Fact]
    public async Task BackgroundNotificationCarriesStateExitCodeAndPartialOutput()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent);
        await using var tasks = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Completed(string job, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta("finished answer"); yield return new AgentDone("stop"); }
        async IAsyncEnumerable<AgentEvent> Cancelled(string job, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); yield return new TokenDelta("half done"); await Task.Delay(Timeout.Infinite, ct); }
        var done = tasks.Start("finished", Guid.NewGuid(), "agent", Completed, parent.Id);
        tasks.NotifyParentOnCompletion(done);
        await tasks.WaitAsync(done);
        var stopped = tasks.Start("stopped", Guid.NewGuid(), "agent", Cancelled, parent.Id);
        tasks.NotifyParentOnCompletion(stopped);
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!tasks.Get(stopped).Output.Contains("half done")) await Task.Delay(10, wait.Token);
        tasks.Cancel(stopped);
        await tasks.WaitAsync(stopped);
        var delivered = SubAgentNotifications.Deliver(store, tasks, parent.Id);
        Assert.Equal(2, delivered.Count);
        var completedJson = Assert.Single(delivered, message => message.Id.ToString("N") == done).Content;
        Assert.Contains("\"state\":\"Completed\"", completedJson);
        Assert.Contains("\"exitCode\":0", completedJson);
        Assert.Contains("\"partial\":false", completedJson);
        Assert.Contains("finished answer", completedJson);
        var cancelledJson = Assert.Single(delivered, message => message.Id.ToString("N") == stopped).Content;
        Assert.Contains("\"state\":\"Cancelled\"", cancelledJson);
        Assert.Contains("\"exitCode\":130", cancelledJson);
        Assert.Contains("\"partial\":true", cancelledJson);
        Assert.Contains("half done", cancelledJson);
        Assert.Contains("did not finish", cancelledJson);
        // Delivery is exactly-once per job generation.
        Assert.Empty(SubAgentNotifications.Deliver(store, tasks, parent.Id));
    }
}
