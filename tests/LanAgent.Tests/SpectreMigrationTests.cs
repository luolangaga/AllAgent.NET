using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class SpectreMigrationTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0', bool shift = false) => new(character, key, shift, false, false);

    [Fact]
    public void WrappedToolUpdatesInvalidateCachedHistoryWithoutReRenderingUnchangedMessages()
    {
        var console = new TestConsole(); console.Profile.Width = 80;
        var mutable = new CountingRenderable();
        var wrapped = new SafeTerminalRenderable(new SafeTerminalRenderable(mutable));
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        string Render(int scroll) => string.Concat(new TranscriptViewport([wrapped], 5, scroll).Render(options, 80).Select(segment => segment.Text));
        Assert.Contains("running", Render(0)); Render(2); Assert.Equal(1, mutable.Renders);
        mutable.Complete(); Assert.Contains("complete", Render(0)); Assert.Equal(2, mutable.Renders);
        Render(0); Assert.Equal(2, mutable.Renders);
    }

    [Fact]
    public void LiveStopsBeforeStandardPromptsAndCanResumeWithoutAnExclusiveOwnerConflict()
    {
        var console = new TestConsole(); console.Profile.Width = 60; console.Profile.Capabilities.Interactive = true;
        using var display = new SpectreScreenDisplay(console);
        display.Start(new Panel(new Text("first frame"))); display.Update(new Panel(new Text("updated frame"))); display.Stop();
        console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("choice", console.Prompt(new SelectionPrompt<string>().AddChoices("choice")));
        display.Start(new Text("resumed frame")); display.Stop();
        Assert.Contains("updated frame", console.Output); Assert.Contains("resumed frame", console.Output);
    }

    [Fact]
    public void SoftWrappedInputMovesVerticallyByTerminalCellsAndKeepsThePreferredColumn()
    {
        var input = new InputState(); input.Set("0123456789abcdefghij");
        var console = new TestConsole(); console.Profile.Width = 10;
        console.Write(new InputText(input));
        input.MoveTo(5); input.Key(Key(ConsoleKey.DownArrow), [], []); Assert.Equal(15, input.Cursor);
        input.Key(Key(ConsoleKey.DownArrow), [], []); Assert.Equal(20, input.Cursor);
        input.Key(Key(ConsoleKey.UpArrow), [], []); Assert.Equal(15, input.Cursor);
        input.Key(Key(ConsoleKey.UpArrow), [], []); Assert.Equal(5, input.Cursor);
    }

    [Fact]
    public void WideCharactersAndTabsUseTheSameGeometryForCaretAndMouse()
    {
        var input = new InputState(); input.Set("中文\ta\n123456789");
        var console = new TestConsole(); console.Profile.Width = 20;
        var view = new InputText(input); console.Write(view);
        input.MoveTo(3); input.Key(Key(ConsoleKey.DownArrow), [], []); Assert.Equal(13, input.Cursor);
        input.Key(Key(ConsoleKey.UpArrow), [], []); Assert.Equal(3, input.Cursor);
        Assert.Equal(1, view.HitTest(4, 0));
    }

    [Fact]
    public void RenderedSingleLineStillSupportsHistoryAndMultilinePasteNeverSubmits()
    {
        var input = new InputState(); var console = new TestConsole(); console.Profile.Width = 80;
        console.Write(new InputText(input));
        input.Key(Key(ConsoleKey.UpArrow), [], ["recent task"]); Assert.Equal("recent task", input.Text);
        input.Set(""); input.Insert("first\r\n/exit\r\n👩‍💻 last\u001b[2J");
        Assert.Equal("first\n/exit\n👩‍💻 last", input.Text);
        Assert.Null(input.Key(Key(ConsoleKey.Enter, shift: true), [], []));
        Assert.Equal("first\n/exit\n👩‍💻 last\n", input.Text);
    }

    [Fact]
    public async Task NativeSpectreSearchEscapeAndTextValidationWorkInTheScreenFacade()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24; console.Profile.Capabilities.Interactive = true;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new(false, false), console);
        console.Input.PushText("second"); console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("second", await screen.ChooseAsync("choose", new[] { "first", "second" }, value => value, default));
        console.Input.PushKey(ConsoleKey.Escape);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => screen.ChooseAsync("choose", new[] { "first" }, value => value, default));
        console.Input.PushKey(ConsoleKey.Enter); console.Input.PushText("valid"); console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("valid", await screen.TextAsync("field", null, false, false));
        console.Input.PushKey(ConsoleKey.Escape);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => screen.TextAsync("field", null, false, false));
    }

    [Fact]
    public async Task HugeWheelBurstCannotLeaveAnInvisibleScrollDebt()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24;
        using var screen = new FullScreenTerminal(store, new(store, workspace.Root, workspace.Root), mcp, new(false, false), console);
        screen.Append(new Text(string.Join('\n', Enumerable.Range(0, 40).Select(index => "row-" + index))));
        console.Write(screen.BuildFrame(80, 24));
        screen.Navigation(new TerminalInput(default, Wheel: 100000)); screen.Navigation(new TerminalInput(default, Wheel: -100000));
        var result = new TestConsole(); result.Profile.Width = 80; result.Write(screen.BuildFrame(80, 24));
        Assert.Contains("row-39", result.Output); Assert.DoesNotContain("阅读历史", result.Output);
    }

    [Fact]
    public async Task EscapePreservesTheNextTypedCharacterAcrossPromptAndChat()
    {
        var console = new TestConsole(); console.Profile.Capabilities.Interactive = true;
        var keys = new Queue<ConsoleKeyInfo>([Key(ConsoleKey.Escape, '\u001b'), Key(ConsoleKey.X, 'x')]);
        var reader = new TerminalKeyReader(() => keys.Count > 0, () => keys.Dequeue());
        var prompt = new SpectrePromptConsole(console, reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt.PromptAsync(new SelectionPrompt<string>().AddChoices("first")));
        Assert.Equal('x', (await reader.ReadAsync(default)).Key.KeyChar);
    }

    [Fact]
    public async Task DelayedBracketedPasteRemainsDataInStandardTextPrompts()
    {
        var console = new TestConsole(); console.Profile.Capabilities.Interactive = true;
        var sequence = "\u001b[200~first\n/exit\u001b[201~";
        var keys = new Queue<ConsoleKeyInfo>(sequence.Select(character => character == '\u001b' ? Key(ConsoleKey.Escape, character) : Key(ConsoleKey.A, character)));
        keys.Enqueue(Key(ConsoleKey.Enter));
        var read = 0; var timer = System.Diagnostics.Stopwatch.StartNew();
        var reader = new TerminalKeyReader(() => keys.Count > 0 && (read != 2 || timer.ElapsedMilliseconds >= 80), () => { read++; return keys.Dequeue(); });
        var prompt = new SpectrePromptConsole(console, reader);
        Assert.Equal("first /exit", await prompt.PromptAsync(new TextPrompt<string>("field")));
        Assert.Empty(keys);
    }

    [Fact]
    public async Task SecretTextPromptMasksInputAndEditableDefaultsCanBeReplaced()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Capabilities.Interactive = true;
        using var screen = new FullScreenTerminal(store, new(store, workspace.Root, workspace.Root), mcp, new(false, false), console);
        console.Input.PushText("secret-value"); console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("secret-value", await screen.TextAsync("secret", null, true, false));
        Assert.DoesNotContain("secret-value", console.Output);
        console.Input.PushKey(ConsoleKey.Backspace); console.Input.PushText("2"); console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("model2", await screen.TextAsync("model", "model1", false, false));
    }

    private sealed class CountingRenderable : IRenderable, IVersionedRenderable
    {
        public int Version { get; private set; }
        public int Renders { get; private set; }
        public void Complete() => Version++;
        public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) { Renders++; return [new Segment(Version == 0 ? "running" : "complete")]; }
    }
}
