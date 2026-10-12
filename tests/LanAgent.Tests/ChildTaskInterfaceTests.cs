using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ChildTaskInterfaceTests
{
    private static readonly ConsoleKeyInfo Escape = new('\0', ConsoleKey.Escape, false, false, false);
    [Fact]
    public void EscapeCancellationNeedsTwoPressesWithinWindowAndResetsOnOtherKeys()
    {
        var confirmation = new EscapeCancelConfirmation();
        Assert.False(confirmation.Read(Escape, 100)); Assert.True(confirmation.Read(Escape, 200));
        Assert.False(confirmation.Read(Escape, 300));
        Assert.False(confirmation.Read(new('a', ConsoleKey.A, false, false, false), 400));
        Assert.False(confirmation.Read(Escape, 500));
        Assert.False(confirmation.Read(Escape, 5500)); Assert.True(confirmation.Read(Escape, 5600));
        var vtEscape = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, true);
        Assert.False(confirmation.Read(vtEscape, 5700)); Assert.True(confirmation.Read(vtEscape, 5800));
    }

    [Fact]
    public void ChildSessionsAreHiddenFromMainHistoryButRemainAccessibleUnderTheirParent()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(w.Root);
        var main = new Conversation { Title = "MAIN" }; var child = new Conversation { Title = "CHILD", ParentConversationId = main.Id };
        store.AddConversation(main); store.AddConversation(child);
        Assert.Equal(main.Id, Assert.Single(SessionOperations.MainSessions(store)).Id);
        Assert.Equal(child.Id, Assert.Single(SessionOperations.Children(store, main.Id)).Id);
        Assert.Equal(main.Id, SessionOperations.Parent(store, child).Id);
    }

    [Fact]
    public async Task ChildTranscriptShowsStoredMessagesAndLiveToolsUsingChatComponents()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(w.Root); await using var tasks = new BackgroundTasks();
        var parent = new Conversation(); var child = new Conversation { ParentConversationId = parent.Id };
        store.AddConversation(parent); store.AddConversation(child);
        store.AddMessage(child.Id, new Message { ConversationId = child.Id, Role = "user", Content = "CHILD-REQUEST" });
        store.AddMessage(child.Id, new Message { ConversationId = child.Id, Role = "assistant", Content = "EARLIER-REPLY" });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AgentIteration(1, 0, 0);
            yield return new TokenDelta("LIVE-REPLY"); yield return new ThinkingDelta("LIVE-THOUGHT");
            yield return new ToolCallStarted("code", "execute_code", "{\"code\":\"r = call_tool('file_read', path='visible.txt')\\nprint(r)\"}");
            yield return new ToolCallStarted("read", "file_read", "{\"path\":\"visible.txt\"}") { ParentId = "code" };
            ready.SetResult(); await release.Task.WaitAsync(ct);
            yield return new ToolCallCompleted("read", "file_read", ToolResult.Success(new { content = "READ-RESULT" }).ToJson());
            yield return new AgentDone("stop");
        }
        var job = tasks.Start("CHILD-TITLE", child.Id, "子 Agent", Run, parent.Id);
        var view = new TaskTranscriptView(store, tasks, job);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var console = new TestConsole(); console.Profile.Width = 120; console.Write(view);
            Assert.Contains("CHILD-REQUEST", console.Output); Assert.Contains("EARLIER-REPLY", console.Output);
            Assert.Contains("LIVE-REPLY", console.Output); Assert.Contains("LIVE-THOUGHT", console.Output);
            Assert.Contains("visible.txt", console.Output); Assert.Contains("(2 行)", console.Output);
            var block = new CodeExecutionBlock("parent-code", "call_tool('start_agent')")
            { TaskLookup = tasks.Get, TaskTranscript = _ => view };
            block.Start(new("start", "start_agent", "{}"));
            block.Complete(new("start", "start_agent", ToolResult.Success(new { taskId = job }).ToJson()));
            console.Clear(true); console.Write(block.Details(0, 0)); Assert.Contains("LIVE-REPLY", console.Output);
            var version = view.Version; release.SetResult(); await tasks.WaitAsync(job); Assert.NotEqual(version, view.Version);
        }
        finally { release.TrySetResult(); }
    }
}
