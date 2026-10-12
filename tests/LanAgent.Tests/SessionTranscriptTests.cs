using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class SessionTranscriptTests
{
    [Fact]
    public void TranscriptHonorsVisibilityAndOptionalThinkingAndTools()
    {
        var session = new Conversation { Title = "中文测试" };
        session.Messages.Add(new Message { Role = "user", Content = "Question" });
        session.Messages.Add(new Message { Role = "user", Content = "Host instructions", IsInternal = true });
        session.Messages.Add(new Message { Role = "assistant", Content = "Answer", ReasoningContent = "Private reasoning", ToolCallsJson = "[{\"id\":\"tool1\",\"function\":{\"name\":\"execute_code\",\"arguments\":\"```payload\"}}]" });
        session.Messages.Add(new Message { Role = "tool", ToolCallId = "tool1", Content = "Tool output" });
        var full = SessionTranscript.Format(session);
        Assert.Contains("# 中文测试", full); Assert.Contains("Private reasoning", full); Assert.Contains("Tool output", full);
        Assert.Contains("````json\n```payload\n````", full); Assert.DoesNotContain("Host instructions", full);
        var plain = SessionTranscript.Format(session, false, false);
        Assert.Contains("Answer", plain); Assert.DoesNotContain("Private reasoning", plain); Assert.DoesNotContain("Tool output", plain);
        session.Messages.Add(new Message { Role = "assistant", Content = "Hidden", IsInternal = true });
        Assert.Equal("Answer", SessionTranscript.LastReply(session.Messages));
    }

    [Fact]
    public async Task ExportWritesUtf8AndRefusesToOverwriteExistingFiles()
    {
        using var workspace = new TestWorkspace(); var session = new Conversation { Title = "中文" };
        var md = Path.Combine(workspace.Root, "session.md"); await SessionTranscript.ExportAsync(session, md);
        Assert.Contains("# 中文", await File.ReadAllTextAsync(md));
        await Assert.ThrowsAsync<IOException>(() => SessionTranscript.ExportAsync(session, md));
        var json = Path.Combine(workspace.Root, "session.json"); await SessionTranscript.ExportAsync(session, json);
        Assert.Contains(session.Id.ToString(), await File.ReadAllTextAsync(json));
    }

    [Fact]
    public void NavigationOnlySelectsChildrenOfTheCurrentParent()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); var unrelated = new Conversation(); var child = new Conversation { ParentConversationId = parent.Id, AgentRole = "explore" };
        store.AddConversation(parent); store.AddConversation(unrelated); store.AddConversation(child);
        var ui = new TerminalUi(true, false);
        Assert.Equal(parent.Id, SessionOperations.Parent(store, child).Id);
        Assert.Equal(child.Id, SessionOperations.Child(store, parent, child.Id.ToString()[..8], ui).Id);
        Assert.Throws<ArgumentException>(() => SessionOperations.Child(store, unrelated, child.Id.ToString(), ui));
        Assert.Throws<ArgumentException>(() => SessionOperations.Parent(store, parent));
    }
}
