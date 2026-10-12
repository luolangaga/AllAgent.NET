using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using System.Text.Json;

namespace LanAgent.Tests;

public sealed class SessionTimelineTests
{
    [Fact]
    public async Task TimelineFiltersInternalMessagesAndRestoresHumanInputWithSnapshotImages()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var first = new Message { ConversationId = session.Id, Role = "user", Content = "expanded body", InputText = "Analyze @deleted.png",
            Images = [ImageAttachment.FromBytes("deleted.png", Convert.FromBase64String(InputInboxTests.Png))] };
        store.AddMessage(session.Id, first);
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", Content = "response" });
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "user", IsInternal = true, Content = "host result" });
        var last = new Message { ConversationId = session.Id, Role = "user", Content = "legacy human input" }; store.AddMessage(session.Id, last);
        Assert.Equal(new[] { last.Id, first.Id }, SessionTimeline.Entries(store, session.Id).Select(m => m.Id));
        Assert.Equal(first.Id, SessionTimeline.Find(store, session.Id, first.Id.ToString()[..8]).Id);
        Assert.Throws<ArgumentException>(() => SessionTimeline.Find(store, Guid.NewGuid(), first.Id.ToString()));
        var restored = await SessionTimeline.RestoreDraftAsync(first);
        var expanded = await PromptReferences.ExpandWithImagesAsync(restored, workspace.Root, allowExplicitExternalImages: true);
        Assert.Equal(InputInboxTests.Png, Assert.Single(expanded.Images).Base64); Assert.StartsWith("Analyze ", restored);
        Assert.DoesNotContain("expanded body", SessionTranscript.Format(session));
        Assert.Contains("Analyze @deleted.png", SessionTranscript.Format(session));
    }
    [Fact]
    public void ForkBeforeUserMessageExcludesItsResponseAndRemapsSummaryBoundary()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation { ContextSummary = "earlier conclusions", ContextUsedTokens = 3000 }; store.AddConversation(session);
        var first = new Message { ConversationId = session.Id, Role = "user", Content = "first" }; store.AddMessage(session.Id, first);
        var reply = new Message { ConversationId = session.Id, Role = "assistant", Content = "first reply" }; store.AddMessage(session.Id, reply);
        var selected = new Message { ConversationId = session.Id, Role = "user", Content = "selected", Images = [ImageAttachment.FromBytes("picture.png", Convert.FromBase64String(InputInboxTests.Png))] }; store.AddMessage(session.Id, selected);
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", Content = "selected reply" });
        session.ContextSummaryThroughId = reply.Id; store.UpdateConversation(session);
        var fork = SessionOperations.Branch(store, session, "fork", selected.Id);
        Assert.Equal(new[] { "first", "first reply" }, fork.Messages.Select(m => m.Content));
        Assert.All(fork.Messages, m => { Assert.Equal(fork.Id, m.ConversationId); Assert.DoesNotContain(session.Messages, original => original.Id == m.Id); });
        Assert.Equal(fork.Messages[1].Id, fork.ContextSummaryThroughId); Assert.Equal("earlier conclusions", fork.ContextSummary); Assert.Null(fork.ContextUsedTokens);
        var early = SessionOperations.Branch(store, session, "early", first.Id);
        Assert.Empty(early.Messages); Assert.Null(early.ContextSummary); Assert.Null(early.ContextSummaryThroughId);
        Assert.Equal(4, session.Messages.Count); Assert.Equal(InputInboxTests.Png, Assert.Single(session.Messages[2].Images).Base64);
    }
    [Theory]
    [InlineData(65, 22)]
    [InlineData(120, 32)]
    public async Task JumpUsesRenderedRowsAcrossWrappedTextAndToolBlocks(int width, int height)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        Message? target = null;
        for (var index = 0; index < 18; index++)
        {
            var message = new Message { ConversationId = session.Id, Role = "user", Content = $"HISTORY-{index:D2}-PROOF " + new string('界', 70) };
            store.AddMessage(session.Id, message);
            if (index == 4) target = message;
            store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", Content = "Response\n" + new string('x', 160) });
            if (index is 1 or 3 or 8)
            {
                var call = "code-" + index;
                store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", ToolCallsJson = JsonSerializer.Serialize(new[] {
                    new { id = call, type = "function", function = new { name = "execute_code", arguments = JsonSerializer.Serialize(new { code = "print('verified')" }) } }
                }) });
                store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "tool", ToolCallId = call, ToolName = "execute_code", Content = "{\"ok\":true,\"data\":\"verified\"}" });
            }
        }
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), console);
        screen.Session(session.Id, "fixture"); screen.JumpToMessage(target!.Id); console.Write(screen.BuildFrame(width, height));
        Assert.Contains("HISTORY-04-PROOF", console.Output); Assert.DoesNotContain("HISTORY-17-PROOF", console.Output);
        var repeated = new TestConsole(); repeated.Profile.Width = width; repeated.Profile.Height = height; repeated.Write(screen.BuildFrame(width, height));
        Assert.Contains("HISTORY-04-PROOF", repeated.Output);
        screen.ScrollToLatest(); var latest = new TestConsole(); latest.Profile.Width = width; latest.Profile.Height = height; latest.Write(screen.BuildFrame(width, height));
        Assert.DoesNotContain("HISTORY-04-PROOF", latest.Output);
    }
}
