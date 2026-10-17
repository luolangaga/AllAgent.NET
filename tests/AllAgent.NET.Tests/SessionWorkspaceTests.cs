using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class SessionWorkspaceTests
{
    [Fact]
    public void CreationSelectionAndReopenKeepWorkspaceIsolation()
    {
        using var temporary = new TestWorkspace();
        var data = Path.Combine(temporary.Root, "data");
        var first = Path.Combine(temporary.Root, "first"); var second = Path.Combine(temporary.Root, "second");
        Guid firstId;
        using (var store = new DataStore(data))
        {
            var a = SessionOperations.Get(store, null, first); firstId = a.Id;
            var b = SessionOperations.Get(store, null, second);
            var legacy = new Conversation(); store.AddConversation(legacy);
            store.AddConversation(new Conversation { ParentConversationId = a.Id, WorkspaceRoot = first });
            Assert.Equal(new[] { a.Id, legacy.Id }.Order(), SessionOperations.MainSessions(store, first).Select(c => c.Id).Order());
            Assert.Equal(new[] { b.Id, legacy.Id }.Order(), SessionOperations.MainSessions(store, second).Select(c => c.Id).Order());
            Assert.Equal(legacy.Id, SessionOperations.Get(store, legacy.Id.ToString(), second).Id);
            Assert.Throws<ArgumentException>(() => SessionOperations.Get(store, a.Id.ToString(), second));
            var branch = SessionOperations.Branch(store, a, null);
            Assert.Equal(a.WorkspaceRoot, branch.WorkspaceRoot);
        }
        using (var reopened = new DataStore(data))
        {
            var restored = SessionOperations.Get(reopened, firstId.ToString(), first + Path.DirectorySeparatorChar);
            Assert.Equal(first, restored.WorkspaceRoot);
            Assert.DoesNotContain(SessionOperations.MainSessions(reopened, first), c => c.WorkspaceRoot == second);
            Assert.Contains(SessionOperations.MainSessions(reopened, first), c => c.WorkspaceRoot == null);
        }
    }
    [Fact]
    public void MatchingUsesCompleteNormalizedPathInsteadOfPrefix()
    {
        using var temporary = new TestWorkspace();
        var path = Path.Combine(temporary.Root, "project");
        var conversation = new Conversation { WorkspaceRoot = path };
        Assert.True(SessionOperations.InWorkspace(conversation, Path.Combine(path, ".")));
        Assert.False(SessionOperations.InWorkspace(conversation, path + "-other"));
        Assert.False(SessionOperations.InWorkspace(conversation, Path.Combine(path, "child")));
        if (OperatingSystem.IsWindows()) Assert.True(SessionOperations.InWorkspace(conversation, path.ToUpperInvariant()));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingWorkspaceIsVisibleInAnyWorkspace(string? recorded)
    {
        using var temporary = new TestWorkspace();
        Assert.True(SessionOperations.InWorkspace(new Conversation { WorkspaceRoot = recorded }, temporary.Root));
    }
}
