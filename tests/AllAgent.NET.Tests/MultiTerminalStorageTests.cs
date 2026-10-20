using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public class MultiTerminalStorageTests
{
    [Fact]
    public async Task ConcurrentStoresPreserveHistorySettingsAndUsage()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            using var store = new DataStore(directory);
            var conversation = new Conversation { Title = "worker " + worker };
            store.AddConversation(conversation);
            for (var i = 0; i < 10; i++)
            {
                store.AddMessage(conversation.Id, new Message { Role = "user", Content = i.ToString() });
                store.RecordTokenUsage("test", "model", new ModelTokenUsage(2, 3));
                store.SetSetting("worker-" + worker, i.ToString());
            }
        })));
        using var reader = new DataStore(directory);
        Assert.Equal(4, reader.Conversations.Count);
        Assert.All(reader.Conversations, conversation => Assert.Equal(10, conversation.Messages.Count));
        Assert.Equal(40, Assert.Single(reader.GetTokenUsageStats()).Requests);
        Assert.Equal(200, Assert.Single(reader.GetTokenUsageStats()).TotalTokens);
        var daily = Assert.Single(reader.GetDailyUsageStats());
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), daily.Date);
        Assert.Equal(40, daily.Requests);
        Assert.Equal(200, daily.InputTokens + daily.OutputTokens);
        for (var worker = 0; worker < 4; worker++) Assert.Equal("9", reader.GetSetting("worker-" + worker));
    }

    [Fact]
    public void StaleStoreCannotResurrectDeletionOrOverwriteUpdatedConversation()
    {
        using var workspace = new TestWorkspace();
        using var first = new DataStore(workspace.Root);
        var conversation = new Conversation(); first.AddConversation(conversation);
        using var second = new DataStore(workspace.Root);
        var stale = second.GetConversation(conversation.Id)!;
        conversation.Title = "updated"; first.UpdateConversation(conversation);
        stale.Title = "stale";
        Assert.Throws<InvalidOperationException>(() => second.UpdateConversation(stale));
        first.DeleteConversation(conversation.Id);
        second.SetSetting("theme", "dark");
        using var reader = new DataStore(workspace.Root);
        Assert.Empty(reader.Conversations);
    }

    [Fact]
    public async Task NewHostDoesNotInterruptOrOverwriteAnotherHostsJob()
    {
        using var workspace = new TestWorkspace();
        await using var first = new BackgroundTasks(workspace.Root);
        var release = new TaskCompletionSource();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await release.Task.WaitAsync(ct); yield return new AgentDone("stop"); }
        var id = first.Start("first", Guid.NewGuid(), "test", Run);
        await using var second = new BackgroundTasks(workspace.Root);
        await using var snapshot = new BackgroundTasks(workspace.Root, readOnly: true);
        Assert.True(BackgroundTasks.IsActive(snapshot.Get(id).State));
        async IAsyncEnumerable<AgentEvent> Complete(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); yield return new AgentDone("stop"); }
        var other = second.Start("second", Guid.NewGuid(), "test", Complete);
        await second.WaitAsync(other);
        Assert.True(BackgroundTasks.IsActive(snapshot.Get(id).State));
        release.SetResult(); await first.WaitAsync(id);
        Assert.Equal(BackgroundTaskState.Completed, snapshot.Get(id).State);
        Assert.Equal(BackgroundTaskState.Completed, snapshot.Get(other).State);
    }
}
