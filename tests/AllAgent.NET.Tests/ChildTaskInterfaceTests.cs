using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ChildTaskInterfaceTests
{
    private static readonly ConsoleKeyInfo Escape = new('\0', ConsoleKey.Escape, false, false, false);
    [Fact]
    public void CtrlCNeedsTwoPressesOnEmptyInputAndEscKeepsTheDraft()
    {
        var ctrlC = new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true);
        var input = new InputState();
        Assert.Null(input.Key(ctrlC, [], []));
        Assert.Contains("再按一次", input.Notice);
        Assert.Null(input.Key(new('x', ConsoleKey.X, false, false, false), [], []));
        Assert.Equal("x", input.Text);
        Assert.Null(input.Key(ctrlC, [], []));
        Assert.Equal("", input.Text);
        Assert.Null(input.Key(ctrlC, [], []));
        Assert.Equal("/exit", input.Key(ctrlC, [], []));
        Assert.Equal("/exit", new InputState().Key(new('\u0004', ConsoleKey.D, false, false, true), [], []));
        input.Set("draft"); Assert.Null(input.Key(ctrlC, [], [])); Assert.Equal("", input.Text);
        Assert.Null(input.Key(new('\0', ConsoleKey.Z, false, false, true), [], [])); Assert.Equal("draft", input.Text);
        Assert.Null(input.Key(Escape, [], [])); Assert.Equal("draft", input.Text);
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
            Assert.Contains("visible.txt", console.Output); Assert.Contains("运行中", console.Output);
            var block = new CodeExecutionBlock("parent-code", "call_tool('subagent')")
            { TaskLookup = tasks.Get, TaskTranscript = _ => view };
            block.Start(new("start", "subagent", "{}"));
            block.Complete(new("start", "subagent", ToolResult.Success(new { taskId = job }).ToJson()));
            console.Clear(true); console.Write(block.Details(0, 0)); Assert.Contains("LIVE-REPLY", console.Output);
            var version = view.Version; release.SetResult(); await tasks.WaitAsync(job); Assert.NotEqual(version, view.Version);
        }
        finally { release.TrySetResult(); }
    }
}
