using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Host plumbing that lets several workspaces stay live at once: the bridge swaps the active
/// workspace's method set, checkpoints isolate concurrent sessions, and finished runs are reported.</summary>
public sealed class ConcurrentSessionHostTests
{
    [Fact]
    public async Task InstallingAnotherWorkspaceReplacesItsMethodsAndKeepsHostLevelOnes()
    {
        var dispatcher = new BridgeDispatcher();
        var workspace = new BridgeDispatcher();
        var terminal = new BridgeDispatcher();
        workspace.Register<string>("conversations.list", _ => Task.FromResult("workspace"));
        workspace.Register<string>("workspace.pickFolder", _ => Task.FromResult("workspace-only"));
        terminal.Register<string>("conversations.list", _ => Task.FromResult("terminal"));
        dispatcher.Register<string>("conversations.running", _ => Task.FromResult("host"));

        dispatcher.Install("bundle", workspace.Handlers);
        Assert.Contains("workspace", await dispatcher.InvokeAsync("""{"id":"1","method":"conversations.list"}"""));
        Assert.Contains("workspace-only", await dispatcher.InvokeAsync("""{"id":"2","method":"workspace.pickFolder"}"""));

        dispatcher.Install("bundle", terminal.Handlers);
        var switched = await dispatcher.InvokeAsync("""{"id":"3","method":"conversations.list"}""");
        Assert.Contains("terminal", switched);
        // Methods the installed set does not own are untouched, and the old set's extras are gone.
        Assert.Contains("host", await dispatcher.InvokeAsync("""{"id":"4","method":"conversations.running"}"""));
        Assert.Contains("Unknown method", await dispatcher.InvokeAsync("""{"id":"5","method":"workspace.pickFolder"}"""));
    }

    [Fact]
    public void ConcurrentSessionsKeepSeparateCheckpointTurnsInOneWorkspace()
    {
        using var w = new TestWorkspace(); var c = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        c.Begin(first, "first turn"); c.Begin(second, "second turn");
        Assert.Throws<InvalidOperationException>(() => c.Begin(first, "duplicate"));

        // Tool runs carry their session, so each write lands in its own turn's snapshot.
        using (AgentExecutionScope.Enter(child: false, first))
        {
            c.BeforeWrite("first.txt"); File.WriteAllText(Path.Combine(w.Root, "first.txt"), "first");
            c.AfterWrite("first.txt"); c.MarkExternalAction();
        }
        using (AgentExecutionScope.Enter(child: false, second))
        {
            c.BeforeWrite("second.txt"); File.WriteAllText(Path.Combine(w.Root, "second.txt"), "second");
            c.AfterWrite("second.txt");
        }
        c.End(first); c.End(second);

        var turns = c.List(first).Concat(c.List(second)).OrderBy(turn => turn.SessionId == first ? 0 : 1).ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal("first.txt", Assert.Single(turns[0].Files).Path);
        Assert.True(turns[0].ExternalActions);
        Assert.Equal("second.txt", Assert.Single(turns[1].Files).Path);
        Assert.False(turns[1].ExternalActions);
    }

    [Fact]
    public async Task ASecondRestoreIsRefusedWhileAnyTurnIsStillWriting()
    {
        using var w = new TestWorkspace(); var c = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var running = Guid.NewGuid();
        var turn = c.Begin(running, "running turn");
        c.End(running);
        c.Begin(Guid.NewGuid(), "other turn");
        Assert.Throws<InvalidOperationException>(() => c.Restore(running, turn.Id, redo: false));
        await Task.CompletedTask;
    }

    [Fact]
    public void JournalSnapshotKeepsItsWireShapeAndVersionGrowsWithEvents()
    {
        var message = new AllAgent.NET.Core.Models.Message { Role = "user", Content = "hello", InputText = "hello" };
        var journal = new BridgeStreamJournal([message], "read") { StreamId = "stream-1" };
        Assert.Equal(0, journal.Version);
        journal.Append(new TokenDelta("中"));
        journal.Append(new TokenDelta("文"));
        Assert.Equal(2, journal.Version);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(journal.Snapshot(evt => evt.GetType().Name, evt => evt), AllAgent.NET.Core.Bridge.JsonOptions.Default));
        var history = document.RootElement.GetProperty("history").EnumerateArray().Single();
        Assert.Equal("user", history.GetProperty("role").GetString());
        Assert.Equal("hello", history.GetProperty("content").GetString());
        Assert.True(history.TryGetProperty("toolCallsJson", out _));
        Assert.Equal("stream-1", document.RootElement.GetProperty("streamId").GetString());
        // Adjacent deltas coalesce, and the retained events replay the whole tail for a late viewer.
        Assert.Equal("中文", Assert.Single(journal.Events()).Event is TokenDelta token ? token.Text : "");
    }
}
