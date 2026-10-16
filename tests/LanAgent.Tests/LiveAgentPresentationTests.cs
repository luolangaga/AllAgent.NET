using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class LiveAgentPresentationTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void AgentSummaryKeepsMultilinePromptsOnOneRow(int width)
    {
        var console = new TestConsole(); console.Profile.Width = width;
        console.Write(new AgentSummaryLine("项目：C:\\workspace\\website\n开工前必须 " + new string('界', 200), new Style(Color.Default)));
        var output = console.Output.TrimEnd('\r', '\n');
        Assert.DoesNotContain("\n", output);
        Assert.EndsWith("…", output);
        Assert.True(new Segment(output).CellCount() <= width);
    }

    [Fact]
    public async Task ChildTranscriptToolsOpenLiveNestedDetailsAndEscapeReturnsToChild()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(w.Root); await using var tasks = new BackgroundTasks();
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var parent = new Conversation(); var child = new Conversation { ParentConversationId = parent.Id };
        store.AddConversation(parent); store.AddConversation(child);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ToolCallStarted("child-code", "execute_code", "{\"code\":\"print(call_tool('file_read', path='visible.txt'))\"}");
            yield return new ToolCallStarted("child-read", "file_read", "{\"path\":\"visible.txt\"}") { ParentId = "child-code" };
            ready.SetResult(); await release.Task.WaitAsync(ct);
            yield return new ToolCallCompleted("child-read", "file_read", ToolResult.Success(new { content = "FULL-CHILD-RESULT" }).ToJson());
            yield return new AgentDone("stop");
        }
        var job = tasks.Start("CHILD-TITLE", child.Id, "agent", Run, parent.Id);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var console = new TestConsole(); console.Profile.Width = 120; console.Profile.Height = 50;
            using var screen = new FullScreenTerminal(store, new(store, w.Root, w.Root, w.Root), mcp, new(false, false), console);
            screen.TaskLookup = tasks.Get;
            var view = new TaskTranscriptView(store, tasks, job, registerExecution: screen.RegisterExecution);
            screen.TaskTranscript = _ => view;
            var input = new InputState(); input.Set("KEEP-DRAFT"); screen.SetInput(input, [], "ready");
            var block = new CodeExecutionBlock("parent-code", "call_tool('subagent')");
            block.Start(new("delegate", "subagent", "{}"));
            block.Complete(new("delegate", "subagent", ToolResult.Success(new { taskId = job }).ToJson()));
            screen.RegisterExecution(block); screen.Append(block);
            void Click(string text)
            {
                var lines = Segment.SplitLines(screen.RenderFrame(120, 50), 120, 50);
                var y = lines.FindIndex(line => line.Any(segment => segment.Text.Contains(text)));
                Assert.True(y >= 0, "Missing clickable text: " + text);
                var x = 0; foreach (var segment in lines[y]) { if (segment.Text.Contains(text)) break; x += segment.CellCount(); }
                var down = new TerminalInput(default, Pointer: new(x, y, PointerAction.Down));
                var up = new TerminalInput(default, Pointer: new(x, y, PointerAction.Up));
                if (!screen.InspectorInput(down)) screen.SelectInput(down, input);
                if (!screen.InspectorInput(up)) screen.SelectInput(up, input);
            }
            string Frame() => string.Concat(screen.RenderFrame(120, 50).Select(segment => segment.Text));
            Click("CHILD-TITLE"); Assert.Contains("子代理消息", Frame());
            Click("visible.txt"); Assert.Contains("工具执行详情", Frame());
            release.SetResult(); await tasks.WaitAsync(job);
            Assert.Contains("FULL-CHILD-RESULT", Frame());
            screen.InspectorInput(new(new('\u001b', ConsoleKey.Escape, false, false, false)));
            Assert.Contains("子代理消息", Frame()); Assert.Equal("KEEP-DRAFT", input.Text);
            screen.InspectLatest(); Assert.Contains("call_tool('subagent')", Frame());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task PendingBatchShowsLatestToolAndOpensEachChildBeforeDispatchReturns()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(w.Root);
        await using var tasks = new BackgroundTasks();
        var parent = new Conversation(); store.AddConversation(parent);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AgentIteration(1, 0, 0);
            yield return new TokenDelta("EARLIER-REPLY");
            yield return new ToolCallStarted("code-" + id, "execute_code", "{\"code\":\"print(call_tool('file_read', path='latest.txt'))\"}");
            yield return new ToolCallStarted("read-" + id, "file_read", "{\"path\":\"latest.txt\"}") { ParentId = "code-" + id };
            yield return new ToolCallCompleted("read-" + id, "file_read", ToolResult.Success(new { content = "LATEST-TOOL-OUTPUT" }).ToJson());
            if (Interlocked.Increment(ref started) == 3) ready.SetResult();
            await release.Task.WaitAsync(ct);
            yield return new TokenDelta("FINAL-REPLY"); yield return new AgentDone("stop");
        }
        string Launch(string title)
        {
            var child = new Conversation { Title = title, ParentConversationId = parent.Id }; store.AddConversation(child);
            store.AddMessage(child.Id, new Message { ConversationId = child.Id, Role = "user", Content = "REQUEST-" + title });
            return tasks.Start(title, child.Id, "子 Agent", Run, parent.Id);
        }
        using (AgentExecutionScope.EnterTool("dispatch")) { Launch("FIRST"); Launch("SECOND"); }
        Launch("UNRELATED"); // A separate tool call must not appear in this batch.
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var block = new CodeExecutionBlock("parent-code", "print(call_tool('subagent', tasks=[]))")
            {
                TaskLookup = tasks.Get,
                InvocationTasks = id => tasks.List.Where(task => task.ParentToolCallId == id).OrderBy(task => task.CreatedAt).ToArray(),
                TaskTranscript = task => new TaskTranscriptView(store, tasks, task.Id),
                TaskPreview = task => TaskTranscriptView.LatestStep(store, tasks, task, true)
            };
            block.Start(new("dispatch", "subagent", "{\"tasks\":[]}"));
            Assert.Null(block.Invocations[0].Result); Assert.Equal(2, block.DetailCount(0));
            string Render(IRenderable content) { var console = new TestConsole(); console.Profile.Width = 100; console.Write(content); return console.Output; }
            var preview = Render(block);
            Assert.Contains("LATEST-TOOL-OUTPUT", preview); Assert.Contains("latest.txt", preview);
            Assert.Contains("╭", preview); Assert.Contains("╰", preview);
            Assert.Contains("点击查看完整执行消息与结果", preview);
            Assert.DoesNotContain("EARLIER-REPLY", preview); Assert.DoesNotContain("UNRELATED", preview);
            Assert.Contains("REQUEST-FIRST", Render(block.Details(0, 0)));
            Assert.Contains("REQUEST-SECOND", Render(block.Details(0, 1)));
            Assert.Contains("call_tool", Render(block.Details(-1, 0))); // Ctrl+E still shows dispatch code.
            var version = block.Version;
            release.SetResult(); await Task.WhenAll(tasks.List.Select(task => tasks.WaitAsync(task.Id)));
            Assert.NotEqual(version, block.Version);
            Assert.Contains("FINAL-REPLY", Render(block));
            Assert.Contains("已完成", Render(block));
            Assert.DoesNotContain("正在执行工具", Render(block));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void RunningInputCompletesTaskCommands()
    {
        var input = new InputState(); input.Set("/tasks s");
        var hints = input.Suggestions(TerminalUi.RunningHints);
        Assert.Contains(hints, hint => hint.Text == "/tasks show");
        input.Key(new('\t', ConsoleKey.Tab, false, false, false), hints, []);
        Assert.StartsWith("/tasks show", input.Text);
    }

    [Fact]
    public async Task CompletedBatchResolvesEveryTaskWithoutLiveCorrelation()
    {
        await using var tasks = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new AgentDone("stop"); }
        var first = tasks.Start("FIRST", Guid.NewGuid(), "agent", Run);
        var second = tasks.Start("SECOND", Guid.NewGuid(), "agent", Run);
        await tasks.WaitAsync(first); await tasks.WaitAsync(second);
        var block = new CodeExecutionBlock("code", "call_tool('subagent')") { TaskLookup = tasks.Get };
        block.Start(new("dispatch", "subagent", "{}"));
        block.Complete(new("dispatch", "subagent", ToolResult.Success(new { results = new[] { new { taskId = first }, new { taskId = second } }, taskId = (string?)null }).ToJson()));
        Assert.Equal(2, block.DetailCount(0));
        Assert.Equal(first, block.SelectedTask(0, 0)?.Id); Assert.Equal(second, block.SelectedTask(0, 1)?.Id);
    }
}
