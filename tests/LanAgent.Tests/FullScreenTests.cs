using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class FullScreenTests
{
    [Fact]
    public async Task WheelScrollsTranscriptPreservesReadingPositionAndReturnsToLatest()
    {
        static ConsoleKeyInfo Key(char c) => new(c, c == '\u001b' ? ConsoleKey.Escape : ConsoleKey.A, false, false, false);
        var keys = new Queue<ConsoleKeyInfo>("\u001b[<64;10;5M\u001b[<65;10;5M".Select(Key)); var reader = new TerminalKeyReader(() => keys.Count > 0, keys.Dequeue);
        Assert.Equal(3, (await reader.ReadAsync(default)).Wheel); Assert.Equal(-3, (await reader.ReadAsync(default)).Wheel); Assert.Empty(keys);
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); var catalog = new SkillCatalog(store, w.Root, w.Root, w.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), console);
        screen.Append(new Text(string.Join('\n', Enumerable.Range(0, 100).Select(i => "line-" + i))));
        string Render() { var output = new TestConsole(); output.Profile.Width = 80; output.Profile.Height = 24; output.Write(screen.BuildFrame(80, 24)); return output.Output; }
        Assert.Contains("line-99", Render()); screen.Navigation(new TerminalInput(default, Wheel: 8)); var before = Render(); Assert.DoesNotContain("line-99", before);
        screen.Append(new Text("new line")); var after = Render(); Assert.Contains("line-90", before); Assert.Contains("line-90", after); Assert.DoesNotContain("new line", after); Assert.Contains("阅读历史", after);
        screen.Navigation(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, true)); Assert.Contains("new line", Render());
        screen.Navigation(new TerminalInput(default, Wheel: 3)); screen.ScrollToLatest(); Assert.Contains("new line", Render());
        var state = new InputState(); screen.SetInput(state, [], new string('x', 160)); screen.TaskNotice = () => "后台待处理 1 · /tasks | "; var footer = Render(); Assert.Contains("后台待处理", footer); Assert.Contains("F1 菜单", footer); Assert.Equal(24, footer.Replace("\r", "").Split('\n').Length);
    }
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    [InlineData(45, 18)]
    public async Task ScreenKeepsInputAndFooterVisibleAndFitsTerminal(int width, int height)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root); catalog.SeedBundled();
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false) { Workspace = workspace.Root }, console);
        var conversation = new Conversation { Title = "测试会话" }; store.AddConversation(conversation); screen.Session(conversation.Id, "local-model");
        screen.Append(new Text(string.Join('\n', Enumerable.Range(0, 100).Select(i => "第" + i + "行内容"))));
        var input = new InputState(); input.Set("帮我检查这个项目"); screen.SetInput(input, [], "准备就绪");
        console.Write(screen.BuildFrame(width, height));
        Assert.Contains("帮我检查这个项目", console.Output); Assert.Contains("准备就绪", console.Output);
        Assert.DoesNotContain("第0行内容", console.Output); Assert.Contains("第99行内容", console.Output);
        var lines = console.Output.Replace("\r", "").Split('\n');
        Assert.InRange(lines.Length, height, height + 1);
        Assert.All(lines, line => Assert.True(new Segment(line).CellCount() <= width, line));
        if (width >= 100) Assert.Contains("会话信息", console.Output); else Assert.DoesNotContain("会话信息", console.Output);
        ExportSnapshot(screen, width, height, "conversation");
        screen.Navigation(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
        var scrolled = new TestConsole(); scrolled.Profile.Width = width; scrolled.Profile.Height = height;
        scrolled.Write(screen.BuildFrame(width, height)); Assert.DoesNotContain("第99行内容", scrolled.Output);
    }

    [Fact]
    public async Task PermissionDialogDecodesUnicodeDiffAndAlwaysShowsChoices()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "bird.html"), "旧内容\n保持原样\n");
        var arguments = JsonSerializer.SerializeToElement(new { path = "bird.html", content = "小鸟骑自行车 🐦\n保持原样\n", overwrite = true });
        var preview = ApprovalPreview.Create(new ToolApprovalRequest("file_write", ToolRisk.Write, arguments), workspace.Root);
        Assert.Contains(preview.Lines, line => line.Text.Contains("- 旧内容")); Assert.Contains(preview.Lines, line => line.Text.Contains("+ 小鸟骑自行车"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), console);
        screen.SetDialog(new Panel(preview.Render()).Border(BoxBorder.Rounded)); console.Write(screen.BuildFrame(80, 24));
        Assert.Contains("小鸟骑自行车", console.Output); Assert.Contains("覆盖已有文件", console.Output);
        Assert.Contains("允许这一次", console.Output); Assert.Contains("拒绝", console.Output); Assert.DoesNotContain("\\u", console.Output);
        Assert.DoesNotContain("输入任务", console.Output);
        ExportSnapshot(screen, 80, 24, "approval");
    }

    [Fact]
    public async Task EscapeDoesNotConsumeFollowingCharactersAndPasteNeverSubmits()
    {
        static ConsoleKeyInfo Key(char c) => new(c, c == '\u001b' ? ConsoleKey.Escape : c == '\n' ? ConsoleKey.Enter : ConsoleKey.A, false, false, false);
        var keys = new Queue<ConsoleKeyInfo>("\u001b[2x".Select(Key)); var reader = new TerminalKeyReader(() => keys.Count > 0, keys.Dequeue);
        Assert.Equal(ConsoleKey.Escape, (await reader.ReadAsync(default)).Key.Key);
        Assert.Equal('[', (await reader.ReadAsync(default)).Key.KeyChar); Assert.Equal('2', (await reader.ReadAsync(default)).Key.KeyChar); Assert.Equal('x', (await reader.ReadAsync(default)).Key.KeyChar);
        keys = new Queue<ConsoleKeyInfo>("\u001b[200~first\n/exit\nlast\u001b[201~".Select(Key)); reader = new TerminalKeyReader(() => keys.Count > 0, keys.Dequeue);
        var pasted = await reader.ReadAsync(default); Assert.Equal("first\n/exit\nlast", pasted.Paste); Assert.Empty(keys);
        var input = new InputState(); input.Insert(pasted.Paste!); Assert.Equal("first\n/exit\nlast", input.Text);
        Assert.Null(input.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, true, false, false), [], [])); Assert.EndsWith("\n", input.Text);
        using var cancel = new CancellationTokenSource(50); reader = new TerminalKeyReader(() => false, () => default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancel.Token));
    }

    [Fact]
    public void UnicodeCaretEditsWholeGraphemesAndEscRestoresHistoryDraft()
    {
        var input = new InputState(); input.Set("中👩‍💻e\u0301");
        input.Key(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, false, false, false), [], []); Assert.Equal("中👩‍💻", input.Text);
        input.Key(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false), [], []); Assert.Equal(1, input.Cursor);
        input.Key(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false), [], []); Assert.Equal("中", input.Text);
        input.Set("draft"); input.Key(new ConsoleKeyInfo('\0', ConsoleKey.R, false, false, true), [], ["draft older"]);
        input.Key(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), [], []); Assert.Equal("draft", input.Text);
        input.Set(""); input.Key(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false), [], []); Assert.Equal(0, input.Cursor);
    }
    [Fact]
    public async Task ReferencesReadOnlyWorkspaceFilesAndStayBounded()
    {
        using var w = new TestWorkspace(); await File.WriteAllTextAsync(Path.Combine(w.Root, "some file.txt"), new string('中', 9000));
        var expanded = await PromptReferences.ExpandAsync("检查 @\"some file.txt\"", w.Root);
        Assert.Contains("untrusted task data", expanded); Assert.Contains("Preview truncated", expanded); Assert.Equal(4000, expanded.Count(c => c == '中'));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PromptReferences.ExpandAsync("inspect @../secret.txt", w.Root));
        await Assert.ThrowsAsync<FileNotFoundException>(() => PromptReferences.ExpandAsync("inspect @missing.txt", w.Root));
    }
    [Fact]
    public async Task InteractionToolWaitsForHostAnswerAndNeverInventsOne()
    {
        var completion = new TaskCompletionSource<string?>(); var received = "";
        var tools = new AgentInteractionTools((question, _) => { received = question.Question; return completion.Task; });
        var waiting = tools.AskAsync("选择什么？", ["A", "B"]); Assert.False(waiting.IsCompleted); Assert.Equal("选择什么？", received);
        completion.SetResult("自定义答案"); var result = await waiting; Assert.True(result.Ok); using var json = JsonDocument.Parse(result.ToJson()); Assert.Equal("自定义答案", json.RootElement.GetProperty("data").GetProperty("answer").GetString());
        Assert.False((await new AgentInteractionTools(null).AskAsync("question", [])).Ok);
        Assert.False((await tools.AskAsync("", [])).Ok);
        Assert.False((await new AgentInteractionTools((_, _) => Task.FromResult<string?>(null)).AskAsync("question", [])).Ok);
    }
    [Fact]
    public void ModelOutputCannotExecuteTerminalEscapeSequences()
    {
        var text = "中文\u001b[2J继续\u001b]52;c;clipboard\a\u009bhidden";
        Assert.Equal("中文继续hidden", TerminalSafeText.Clean(text));
    }
    [Fact]
    public async Task WelcomeAndThemesRenderUsingTheSameLiveScreenComponents()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 120; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false) { Workspace = w.Root }, console);
        console.Write(screen.BuildFrame(120, 35)); Assert.Contains("第一次使用", console.Output); Assert.Contains("会话信息", console.Output);
        ExportSnapshot(screen, 120, 35, "welcome");
        screen.Theme = TerminalTheme.Get("light"); ExportSnapshot(screen, 120, 35, "welcome-light");
        var small = new TestConsole(); small.Profile.Width = 28; small.Profile.Height = 10; small.Write(screen.BuildFrame(28, 10));
        Assert.Contains("请放大", small.Output); Assert.Equal(10, small.Output.Replace("\r", "").Split('\n').Length);
    }
    [Fact]
    public async Task MultilineArrowsMoveCaretAndFileCompletionQuotesSpaces()
    {
        var input = new InputState(); input.Set("第一行\n👩‍💻 second"); input.Key(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), [], ["must not replace draft"]);
        Assert.Equal("第一行\n👩‍💻 second", input.Text); Assert.Equal(3, input.Cursor);
        input.Key(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), [], []); Assert.Equal(11, input.Cursor);
        using var w = new TestWorkspace(); await File.WriteAllTextAsync(Path.Combine(w.Root, "some file.txt"), "reference");
        var editor = new InputEditor(Path.Combine(w.Root, "history.json"), w.Root, () => CommandCatalog.Builtins); input.Set("read @so");
        var ready = System.Diagnostics.Stopwatch.StartNew();
        while (editor.Hints(input).Count == 0 && ready.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        input.Complete(Assert.Single(editor.Hints(input))); Assert.Equal("read @\"some file.txt\"", input.Text);
        Assert.Contains("reference", await PromptReferences.ExpandAsync(input.Text, w.Root));
    }
    private static void ExportSnapshot(FullScreenTerminal screen, int width, int height, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LANAGENT_TUI_SNAPSHOTS"); if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var recorder = console.CreateRecorder(); recorder.Write(screen.BuildFrame(width, height));
        File.WriteAllText(Path.Combine(directory, $"{name}-{width}x{height}.html"), recorder.ExportHtml());
    }
}
