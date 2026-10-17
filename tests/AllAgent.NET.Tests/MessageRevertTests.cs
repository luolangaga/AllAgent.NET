using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class MessageRevertTests
{
    [Fact]
    public async Task CompactRejectsStagedHistoryBeforeAnySummaryOrArchive()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var (session, users) = History(store, checkpoints, w.Root);
        checkpoints.RevertRange(store, session.Id, users[1].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => SessionOperations.CompactAsync(store, session, new ApiConfig(), new TerminalUi(false, false), 100000, "", CancellationToken.None));
        Assert.NotNull(session.Revert); Assert.Equal(7, session.Messages.Count); Assert.Single(store.Conversations);
        Assert.Equal("reply 1", SessionTranscript.LastReply(store.GetVisibleMessages(session.Id)));
    }
    internal static (Conversation Session, List<Message> Users) History(DataStore store, WorkspaceCheckpoints checkpoints, string root)
    {
        var session = new Conversation(); store.AddConversation(session);
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "user", Content = "baseline" });
        File.WriteAllText(Path.Combine(root, "shared.txt"), "original");
        var users = new List<Message>();
        for (var index = 1; index <= 3; index++)
        {
            checkpoints.Begin(session.Id, "turn " + index, session.Messages);
            var user = new Message { ConversationId = session.Id, Role = "user", Content = "task " + index };
            users.Add(user); store.AddMessage(session.Id, user);
            checkpoints.BeforeWrite("shared.txt"); File.WriteAllText(Path.Combine(root, "shared.txt"), "version " + index); checkpoints.AfterWrite("shared.txt");
            if (index == 1) { checkpoints.BeforeWrite("created.txt"); File.WriteAllText(Path.Combine(root, "created.txt"), "created"); checkpoints.AfterWrite("created.txt"); }
            store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", Content = "reply " + index });
            checkpoints.End(session.Messages);
        }
        return (session, users);
    }
    [Fact]
    public void StageMovesBothDirectionsRetainsHistoryAndClearRestoresAllFiles()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var (session, users) = History(store, checkpoints, w.Root);
        Assert.Equal(2, checkpoints.RevertRange(store, session.Id, users[1].Id).Count);
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(w.Root, "shared.txt"))); Assert.True(File.Exists(Path.Combine(w.Root, "created.txt")));
        Assert.Equal(7, store.GetMessages(session.Id).Count); Assert.Equal(3, store.GetVisibleMessages(session.Id).Count);
        checkpoints.RevertRange(store, session.Id, users[0].Id);
        Assert.Equal("original", File.ReadAllText(Path.Combine(w.Root, "shared.txt"))); Assert.False(File.Exists(Path.Combine(w.Root, "created.txt")));
        checkpoints.RevertRange(store, session.Id, users[2].Id);
        Assert.Equal("version 2", File.ReadAllText(Path.Combine(w.Root, "shared.txt"))); Assert.True(File.Exists(Path.Combine(w.Root, "created.txt")));
        checkpoints.ClearRevert(store, session.Id);
        Assert.Null(session.Revert); Assert.Equal("version 3", File.ReadAllText(Path.Combine(w.Root, "shared.txt")));
        Assert.Equal(7, store.GetVisibleMessages(session.Id).Count); Assert.All(checkpoints.List(session.Id), c => Assert.False(c.Undone));
    }
    [Fact]
    public void RestartRetainsStageAndCommitInvalidatesRemovedRedoHistory()
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data"); Guid sessionId, boundary;
        using (var store = new DataStore(directory))
        {
            var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), directory);
            var (session, users) = History(store, checkpoints, w.Root); sessionId = session.Id; boundary = users[1].Id;
            checkpoints.RevertRange(store, sessionId, boundary);
        }
        using var reopened = new DataStore(directory); var sessionAfter = reopened.GetConversation(sessionId)!;
        Assert.Equal(boundary, sessionAfter.Revert!.MessageId); Assert.Equal(7, reopened.GetMessages(sessionId).Count);
        Assert.Equal(3, reopened.GetVisibleMessages(sessionId).Count);
        Assert.Equal(2, SessionTimeline.Entries(reopened, sessionId).Count);
        var fork = SessionOperations.Branch(reopened, sessionAfter, "visible branch"); Assert.Equal(3, fork.Messages.Count); Assert.Null(fork.Revert);
        Assert.DoesNotContain("task 2", SessionTranscript.Format(sessionAfter));
        reopened.CommitRevert(sessionId, w.Root + Path.DirectorySeparatorChar);
        Assert.Equal(3, reopened.GetMessages(sessionId).Count); Assert.Null(sessionAfter.Revert); Assert.Equal(2, sessionAfter.InvalidatedCheckpoints.Count);
    }
    [Fact]
    public void ConflictInLaterRangeLeavesEveryFileAndConversationUntouched()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var (session, users) = History(store, checkpoints, w.Root);
        File.WriteAllText(Path.Combine(w.Root, "created.txt"), "human edit");
        Assert.Throws<IOException>(() => checkpoints.RevertRange(store, session.Id, users[0].Id));
        Assert.Equal("version 3", File.ReadAllText(Path.Combine(w.Root, "shared.txt"))); Assert.Equal("human edit", File.ReadAllText(Path.Combine(w.Root, "created.txt")));
        Assert.Null(session.Revert); Assert.False(File.Exists(checkpoints.RevertJournalPath)); Assert.All(checkpoints.List(session.Id), c => Assert.False(c.Undone));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentPartialTransactionRecoversAccordingToDurableConversationState(bool committed)
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data"); Guid sessionId;
        using (var store = new DataStore(directory))
        {
            var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), directory);
            var (session, users) = History(store, checkpoints, w.Root); sessionId = session.Id;
            var range = checkpoints.List(sessionId);
            var files = new[] { new RevertFile("shared.txt", Encoding.UTF8.GetBytes("version 3"), Encoding.UTF8.GetBytes("original")), new RevertFile("created.txt", Encoding.UTF8.GetBytes("created"), null) };
            var after = new SessionRevertState(users[0].Id, w.Root, range.Select(c => c.Id).ToArray(), files);
            var journal = new WorkspaceCheckpoints.RevertJournal(sessionId, DataStore.RevertHistoryHash(session.Messages), null, after, files, range.Select(c => new WorkspaceCheckpoints.RevertFlag(c.Id, false, true)).ToArray());
            File.WriteAllText(checkpoints.RevertJournalPath, JsonSerializer.Serialize(journal));
            // Persist a real partial file/checkpoint transaction, then discard both in-memory services.
            File.WriteAllText(Path.Combine(w.Root, "shared.txt"), "original");
            var first = range[0]; first.Undone = true;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(checkpoints.RevertJournalPath)!, first.Id.ToString("N") + ".json"), JsonSerializer.Serialize(first));
            if (committed) { session.Revert = after; store.UpdateConversation(session); }
        }
        using var reopened = new DataStore(directory); var recovery = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), directory);
        Assert.Equal(committed, recovery.RecoverRevert(reopened)); Assert.False(File.Exists(recovery.RevertJournalPath));
        Assert.Equal(committed ? "original" : "version 3", File.ReadAllText(Path.Combine(w.Root, "shared.txt")));
        Assert.Equal(!committed, File.Exists(Path.Combine(w.Root, "created.txt")));
        Assert.All(recovery.List(sessionId), c => Assert.Equal(committed, c.Undone));
        Assert.Equal(committed ? 1 : 7, reopened.GetVisibleMessages(sessionId).Count);
    }
    [Fact]
    public void MidTurnSteerWithoutCheckpointDoesNotClaimExactRevert()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var session = new Conversation(); store.AddConversation(session); checkpoints.Begin(session.Id, "one turn", []);
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "user", Content = "start" });
        var steer = new Message { ConversationId = session.Id, Role = "user", Content = "mid-turn steer" }; store.AddMessage(session.Id, steer);
        checkpoints.End(session.Messages);
        Assert.Throws<InvalidOperationException>(() => checkpoints.RevertRange(store, session.Id, steer.Id)); Assert.Null(session.Revert);
    }
    [Fact]
    public void WindowsReadOnlyDeleteFailureRollsBackEarlierWrites()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var (session, users) = History(store, checkpoints, w.Root); var path = Path.Combine(w.Root, "created.txt");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => checkpoints.RevertRange(store, session.Id, users[0].Id));
            Assert.Equal("version 3", File.ReadAllText(Path.Combine(w.Root, "shared.txt"))); Assert.Equal("created", File.ReadAllText(path));
            Assert.Null(session.Revert); Assert.All(checkpoints.List(session.Id), c => Assert.False(c.Undone)); Assert.False(File.Exists(checkpoints.RevertJournalPath));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}
