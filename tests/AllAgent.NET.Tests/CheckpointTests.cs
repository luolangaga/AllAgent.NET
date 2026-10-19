using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class CheckpointTests
{
    [Fact]
    public async Task FileToolsPersistOriginalBytesAndSupportUndoRedoAcrossRestart()
    {
        using var w = new TestWorkspace(); var path = Path.Combine(w.Root, "proof.txt");
        var original = new byte[] { 0xef, 0xbb, 0xbf, 0x41, 0x0d, 0x0a }; await File.WriteAllBytesAsync(path, original);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var id = Guid.NewGuid(); var before = new Message { Role = "user", Content = "previous message" };
        var turn = checkpoints.Begin(id, "write files", [before]);
        var tools = new SystemTools(new WorkspacePolicy(w.Root), new RuntimeEnvironment(w.Python, w.Pwsh, []), new Dictionary<string, string>(), checkpoints);
        Assert.True((await tools.WriteAsync("proof.txt", "first", true)).Ok);
        Assert.True((await tools.WriteAsync("proof.txt", "second", true)).Ok);
        Assert.True((await tools.WriteAsync("new.txt", "new file")).Ok);
        before.Content = "modified reference must not affect stored snapshot";
        checkpoints.End(id, [new Message { Role = "assistant", Content = "finished" }]);
        var restarted = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var restored = restarted.Restore(id, turn.Id, redo: false);
        Assert.Equal("previous message", Assert.Single(restored.BeforeMessages).Content);
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(Path.Combine(w.Root, "new.txt")));
        Assert.True(Assert.Single(restarted.List(id)).Undone);
        restarted.Restore(id, turn.Id, redo: true);
        Assert.Equal("second", await File.ReadAllTextAsync(path)); Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(w.Root, "new.txt")));
        Assert.Throws<InvalidOperationException>(() => restarted.Restore(id, turn.Id, redo: true));
    }
    [Fact]
    public void ConflictPreflightDoesNotTouchOtherFiles()
    {
        using var w = new TestWorkspace(); var c = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var session = Guid.NewGuid(); var turn = c.Begin(session, "change");
        foreach (var path in new[] { "a.txt", "b.txt" }) { c.BeforeWrite(path); File.WriteAllText(Path.Combine(w.Root, path), "agent"); c.AfterWrite(path); }
        c.End(session); File.WriteAllText(Path.Combine(w.Root, "b.txt"), "user edited afterward");
        Assert.Throws<IOException>(() => c.Restore(session, turn.Id, false));
        Assert.Equal("agent", File.ReadAllText(Path.Combine(w.Root, "a.txt")));
        Assert.Equal("user edited afterward", File.ReadAllText(Path.Combine(w.Root, "b.txt")));
    }
    [Fact]
    public void PendingWriteAndPathEscapeCannotBeSilentlyRestored()
    {
        using var w = new TestWorkspace(); var c = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var session = Guid.NewGuid(); var turn = c.Begin(session, "interrupted");
        Assert.Throws<UnauthorizedAccessException>(() => c.BeforeWrite("../escape.txt"));
        c.BeforeWrite("interrupted.txt"); File.WriteAllText(Path.Combine(w.Root, "interrupted.txt"), "write completed before journal was updated"); c.End(session);
        Assert.Throws<IOException>(() => c.Restore(session, turn.Id, false)); Assert.True(File.Exists(Path.Combine(w.Root, "interrupted.txt")));
    }
    [Fact]
    public void ExternalActionsAreMarkedAndActiveTurnsCannotBeRestored()
    {
        using var w = new TestWorkspace(); var c = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var session = Guid.NewGuid(); var turn = c.Begin(session, "external"); c.MarkExternalAction();
        Assert.Throws<InvalidOperationException>(() => c.Restore(session, turn.Id, false)); c.End(session);
        Assert.True(Assert.Single(c.List(session)).ExternalActions);
    }
}
