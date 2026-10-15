using System.Diagnostics;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class TerminalInteractionTests
{
    [Fact]
    public void ToolRowsRenderPathsWithMarkupCharactersAndFoldLongResults()
    {
        var block = new CodeExecutionBlock("code", "call_tool('file_glob')");
        block.Start(new ToolCallStarted("glob", "file_glob", "{\"pattern\":\"**/*.ts\",\"path\":\"src/[demo]\"}"));
        block.Complete(new ToolCallCompleted("glob", "file_glob", ToolResult.Success(new { paths = new[] { "a.ts", "b.ts", "c.ts", "d.ts" }, totalMatches = 4 }).ToJson()));
        var console = new TestConsole(); console.Profile.Width = 100;
        console.Write(block);
        Assert.Contains("GLOB", console.Output);
        Assert.Contains("src/[demo]", console.Output);
        Assert.Contains("Found 4 files", console.Output);
        Assert.Contains("+2 lines", console.Output);
        Assert.DoesNotContain("╭", console.Output);
    }
    [Theory]
    [InlineData("自定义提供商")]
    [InlineData("提供商😀")]
    [InlineData("https://localhost:8000/v1")]
    public async Task UnicodeDefaultTextCanBeEditedAndSubmitted(string initial)
    {
        var console = new TestConsole(); console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(new ConsoleKeyInfo('新', ConsoleKey.A, false, false, false));
        console.Input.PushKey(ConsoleKey.Enter);
        var prompt = new TextPrompt<string>("名称");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answer = await new SpectrePromptConsole(console, initialText: initial).PromptAsync(prompt, cancellation.Token);
        Assert.Equal(initial[..^1] + "新", answer);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnterOnEmptyOtherOpensAnswerInput(bool multiple)
    {
        var console = new TestConsole(); console.Profile.Capabilities.Interactive = true;
        if (multiple) console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushText("purple"); console.Input.PushKey(ConsoleKey.Enter);
        var reader = new TerminalKeyReader(console.Input.IsKeyAvailable, () => console.Input.ReadKey(true) ?? default);
        var answer = await TerminalQuestionPrompt.AskAsync(console, reader, new("选择颜色", ["红色"], multiple), default);
        if (multiple) Assert.Equal(new[] { "红色", "purple" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(answer!));
        else Assert.Equal("purple", answer);
        Assert.Contains("其他回答", console.Output);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherAcceptsTextInTheSelectionWithoutASecondPrompt(bool multiple)
    {
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 30; console.Profile.Capabilities.Interactive = true;
        if (multiple) console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushText("custom answerx");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);
        var reader = new TerminalKeyReader(console.Input.IsKeyAvailable, () => console.Input.ReadKey(true) ?? default);
        var answer = await TerminalQuestionPrompt.AskAsync(console, reader, new("选择？", ["A"], multiple), default);
        if (multiple) Assert.Equal(new[] { "A", "custom answer" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(answer!));
        else Assert.Equal("custom answer", answer);
        Assert.DoesNotContain("其他回答 ›", console.Output);
    }
    private static TerminalKeyReader Reader(string bytes, Func<string?>? clipboard = null)
    {
        var keys = new Queue<ConsoleKeyInfo>(bytes.Select(c => new ConsoleKeyInfo(c, c == '\u001b' ? ConsoleKey.Escape : ConsoleKey.A, false, false, false)));
        return new(() => keys.Count > 0, keys.Dequeue, clipboard);
    }
    [Fact]
    public async Task RightButtonEmptyImagePasteAndWheelBurstsKeepFollowingKeys()
    {
        var reader = Reader("\u001b[<2;5;19M\u001b[<2;5;19m");
        Assert.Equal(PointerButton.Right, (await reader.ReadAsync(default)).Pointer!.Value.Button);
        Assert.Equal(PointerAction.Up, (await reader.ReadAsync(default)).Pointer!.Value.Action);
        reader = Reader("\u001b[200~\u001b[201~", () => "\"clipboard.png\"");
        Assert.Equal("\"clipboard.png\"", (await reader.ReadAsync(default)).Paste);
        reader = Reader(string.Concat(Enumerable.Repeat("\u001b[<64;8;5M", 40)) + "q");
        Assert.Equal(120, (await reader.ReadAsync(default)).Wheel); Assert.Equal('q', (await reader.ReadAsync(default)).Key.KeyChar);
        var space = new Queue<ConsoleKeyInfo>([new(' ', 0, false, false, false)]);
        reader = new(() => space.Count > 0, space.Dequeue);
        Assert.Equal(ConsoleKey.Spacebar, (await reader.ReadAsync(default)).Key.Key);
        Assert.Contains("Mindblown", LoadingIndicator.Names);
        reader = Reader("\u001b[<0;2;5M" + string.Concat(Enumerable.Range(3, 120).Select(x => $"\u001b[<32;{x};5M")) + "\u001b[<0;122;5m" + "q");
        Assert.Equal(PointerAction.Down, (await reader.ReadAsync(default)).Pointer!.Value.Action);
        var drag = (await reader.ReadAsync(default)).Pointer!.Value;
        Assert.Equal(PointerAction.Drag, drag.Action); Assert.Equal(121, drag.X);
        Assert.Equal(PointerAction.Up, (await reader.ReadAsync(default)).Pointer!.Value.Action);
        Assert.Equal('q', (await reader.ReadAsync(default)).Key.KeyChar);
    }
    [Fact]
    public async Task DragStaysInMessageOrTableColumnAndRightClickCopiesSelectedInput()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        await using var mcp = new McpToolService(store, new SkillExecutor()); var console = new TestConsole(); console.Profile.Width = 120; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, new(store, w.Root, w.Root), mcp, new(false, false), console);
        screen.Append(new Text("FIRST-ONE\nFIRST-TWO")); screen.Append(new Text("SECOND-BLOCK"));
        var copied = ""; var state = new InputState(text => { copied = text; return true; }); state.Set("input text"); screen.SetInput(state, [], "ready");
        var frame = screen.RenderFrame(120, 35); var lines = Segment.SplitLines(frame, 120, 35);
        var y = lines.FindIndex(line => string.Concat(line.Select(segment => segment.Text)).Contains("FIRST-ONE"));
        screen.SelectInput(new(default, Pointer: new(2, y, PointerAction.Down)), state);
        screen.SelectInput(new(default, Pointer: new(119, 32, PointerAction.Drag)), state);
        var text = screen.ExtractSelection((2, y), (119, 32)); Assert.Contains("FIRST-TWO", text); Assert.DoesNotContain("SECOND-BLOCK", text); Assert.DoesNotContain("会话信息", text); Assert.DoesNotContain("输入", text);
        screen.ScrollToLatest();
        var table = new Table().AddColumn("ID").AddColumn("Title").AddColumn("Model"); table.AddRow("id-one", "TITLE-ONE", "MODEL-ONE"); table.AddRow("id-two", "TITLE-TWO", "MODEL-TWO");
        screen.Append(table); frame = screen.RenderFrame(120, 35); lines = Segment.SplitLines(frame, 120, 35);
        y = lines.FindIndex(line => string.Concat(line.Select(segment => segment.Text)).Contains("TITLE-ONE"));
        var row = string.Concat(lines[y].Select(segment => segment.Text)); var x = row.IndexOf("TITLE-ONE", StringComparison.Ordinal);
        screen.SelectInput(new(default, Pointer: new(x, y, PointerAction.Down)), state); screen.SelectInput(new(default, Pointer: new(119, y + 1, PointerAction.Drag)), state);
        text = screen.ExtractSelection((x, y), (119, y + 1)); Assert.Contains("TITLE-ONE", text); Assert.Contains("TITLE-TWO", text); Assert.DoesNotContain("id-", text); Assert.DoesNotContain("MODEL", text);
        state.SelectAll(); Assert.True(await screen.ContextClickAsync(new(default, Pointer: new(5, 28, PointerAction.Down, Button: PointerButton.Right)), state, _ => throw new Exception("Copy must not paste")));
        Assert.Equal("input text", copied);
    }
    [Fact]
    public async Task ActualSpectreMultiSelectionReturnsSeveralAnswersAndOtherText()
    {
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 30; console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Spacebar); console.Input.PushKey(ConsoleKey.DownArrow); console.Input.PushKey(ConsoleKey.Spacebar); console.Input.PushKey(ConsoleKey.Enter);
        var reader = new TerminalKeyReader(console.Input.IsKeyAvailable, () => console.Input.ReadKey(true) ?? default);
        Assert.Equal("[\"A\",\"B\"]", await TerminalQuestionPrompt.AskAsync(console, reader, new("Choose", ["A", "B"], true), default));
        console.Input.PushKey(ConsoleKey.Escape);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TerminalQuestionPrompt.AskAsync(console, reader, new("Choose", ["A", "B"], true), default));
    }
    [Fact]
    public async Task DirectoryHintsDoNotScanSynchronouslyAndReuseIndex()
    {
        using var w = new TestWorkspace(); File.WriteAllText(Path.Combine(w.Root, "matching file.txt"), "proof");
        var editor = new InputEditor(Path.Combine(w.Root, "history.json"), w.Root, () => []); var state = new InputState(); state.Set("@match");
        var timer = Stopwatch.StartNew(); editor.Hints(state); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        while (editor.Hints(state).Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.Single(editor.Hints(state));
        for (var i = 0; i < 100; i++) Assert.Single(editor.Hints(state));
    }
}
