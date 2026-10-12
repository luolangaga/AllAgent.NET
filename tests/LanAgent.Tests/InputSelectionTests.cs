using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class InputSelectionTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool ctrl = false) => new('\0', key, shift, false, ctrl);
    private static void Press(InputState state, ConsoleKey key, bool shift = false, bool ctrl = false) => Assert.Null(state.Key(Key(key, shift, ctrl), [], []));
    [Fact]
    public void SelectCopyCutPasteUndoAndRedoPreserveMultilineUnicode()
    {
        var clipboard = ""; var state = new InputState(text => { clipboard = text; return true; });
        state.Set("第一行\n👩‍💻e\u0301"); Press(state, ConsoleKey.LeftArrow, shift: true);
        Assert.Equal("e\u0301", state.SelectedText); Press(state, ConsoleKey.LeftArrow, shift: true);
        Assert.Equal("👩‍💻e\u0301", state.SelectedText);
        Press(state, ConsoleKey.C, ctrl: true); Assert.Equal("👩‍💻e\u0301", clipboard); Assert.Equal("第一行\n👩‍💻e\u0301", state.Text);
        Press(state, ConsoleKey.X, ctrl: true); Assert.Equal("第一行\n", state.Text);
        Press(state, ConsoleKey.Z, ctrl: true); Assert.Equal(clipboard, state.SelectedText);
        state.Insert("替换\r\n/exit"); Assert.Equal("第一行\n替换\n/exit", state.Text); // Paste does not submit a command.
        Press(state, ConsoleKey.Z, ctrl: true); Assert.Equal("第一行\n👩‍💻e\u0301", state.Text);
        Press(state, ConsoleKey.Y, ctrl: true); Assert.Equal("第一行\n替换\n/exit", state.Text);
        Press(state, ConsoleKey.A, ctrl: true); Assert.Equal(state.Text, state.SelectedText);
        Press(state, ConsoleKey.Backspace); Assert.Empty(state.Text);
        Press(state, ConsoleKey.Z, ctrl: true); Assert.Equal(state.Text, state.SelectedText);
    }
    [Fact]
    public void FailedClipboardAndProtectedFieldsNeverDeleteSelectedText()
    {
        var state = new InputState(_ => false); state.Set("保留内容"); state.SelectAll(); Press(state, ConsoleKey.X, ctrl: true);
        Assert.Equal("保留内容", state.Text); Assert.True(state.HasSelection); Assert.Contains("保留", state.Notice);
        var invoked = false; state = new InputState(_ => { invoked = true; return true; }) { AllowCopy = false };
        state.Set("secret"); state.SelectAll(); Press(state, ConsoleKey.C, ctrl: true); Assert.False(invoked); Assert.Equal("secret", state.Text);
    }
    [Fact]
    public void ShiftNavigationSelectsTextWithoutNavigatingSuggestionsOrHistory()
    {
        var state = new InputState(); state.Set("/models");
        Assert.Null(state.Key(Key(ConsoleKey.LeftArrow, shift: true), CommandCatalog.Builtins, ["must not replace"])); Assert.Equal("s", state.SelectedText);
        Press(state, ConsoleKey.Home, shift: true, ctrl: true); Assert.Equal("/models", state.SelectedText);
        Press(state, ConsoleKey.RightArrow); Assert.Equal(state.Text.Length, state.Cursor); Assert.False(state.HasSelection);
        state.Set("第一行\n第二行"); Press(state, ConsoleKey.Home, shift: true); Assert.Equal("第二行", state.SelectedText);
        Press(state, ConsoleKey.Home, shift: true, ctrl: true); Assert.Equal(state.Text, state.SelectedText);
        Press(state, ConsoleKey.End, ctrl: true); Assert.False(state.HasSelection); Assert.Equal(state.Text.Length, state.Cursor);
        state.Set("one two"); Press(state, ConsoleKey.LeftArrow, shift: true, ctrl: true); Assert.Equal("two", state.SelectedText);
    }
    [Fact]
    public async Task CtrlVAndShiftInsertPasteReplaceSelectionWithoutSubmitting()
    {
        foreach (var key in new[] { Key(ConsoleKey.V, ctrl: true), Key(ConsoleKey.Insert, shift: true) })
        {
            var reader = new TerminalKeyReader(() => true, () => key, () => "粘贴\r\n/exit");
            var pasted = await reader.ReadAsync(default); Assert.Equal("粘贴\n/exit", pasted.Paste);
            var state = new InputState(); state.Set("old"); state.SelectAll(); state.Insert(pasted.Paste!);
            Assert.Equal("粘贴\n/exit", state.Text); Assert.False(state.HasSelection);
        }
    }
    [Fact]
    public void WrappedInputKeepsCaretVisibleHighlightsSelectionAndMasksSecrets()
    {
        var state = new InputState(); state.Set(new string('x', 100) + "尾部"); state.SelectAll();
        var console = new TestConsole(); console.Profile.Width = 20;
        var view = new InputText(state, maxRows: 3); console.Write(view);
        Assert.Contains("尾部", console.Output); Assert.True(console.Output.Split('\n').Length <= 3);
        var segments = view.Render(RenderOptions.Create(console, console.Profile.Capabilities), 20).ToArray();
        Assert.Contains(segments, s => s.Style.Background == Color.Blue); Assert.True(view.HitTest(100, 100) == state.Text.Length);
        var secret = new TestConsole(); secret.Profile.Width = 20; secret.Write(new InputText(state, secret: true)); Assert.DoesNotContain("尾部", secret.Output); Assert.Contains("•", secret.Output);
        Press(state, ConsoleKey.Home, ctrl: true); var first = new TestConsole(); first.Profile.Width = 20; first.Write(new InputText(state, maxRows: 3)); Assert.DoesNotContain("尾部", first.Output);
    }
    [Fact]
    public async Task MouseCoordinatesUseRenderedCellsAndSelectWholeGraphemes()
    {
        var keys = new Queue<ConsoleKeyInfo>("\u001b[<0;5;19M\u001b[<32;9;19M\u001b[<0;9;19m".Select(c => new ConsoleKeyInfo(c, c == '\u001b' ? ConsoleKey.Escape : ConsoleKey.A, false, false, false)));
        var reader = new TerminalKeyReader(() => keys.Count > 0, keys.Dequeue);
        Assert.Equal(PointerAction.Down, (await reader.ReadAsync(default)).Pointer!.Value.Action); Assert.Equal(PointerAction.Drag, (await reader.ReadAsync(default)).Pointer!.Value.Action); Assert.Equal(PointerAction.Up, (await reader.ReadAsync(default)).Pointer!.Value.Action);
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); var catalog = new SkillCatalog(store, w.Root, w.Root, w.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), console);
        var state = new InputState(); state.Set("中👩‍💻e\u0301"); screen.SetInput(state, [], "test"); console.Write(screen.BuildFrame(80, 24));
        var rows = console.Output.Replace("\r", "").Split('\n'); Assert.Contains("中👩‍💻e\u0301", rows[18]);
        screen.SelectInput(new(default, Pointer: new(4, 18, PointerAction.Down)), state);
        screen.SelectInput(new(default, Pointer: new(4 + new Segment("中👩‍💻").CellCount(), 18, PointerAction.Drag)), state);
        screen.SelectInput(new(default, Pointer: new(4 + new Segment("中👩‍💻").CellCount(), 18, PointerAction.Up)), state);
        Assert.Equal("中👩‍💻", state.SelectedText);
        Assert.Empty(keys);
    }
}
