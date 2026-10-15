using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;
using Xunit.Abstractions;

namespace LanAgent.Tests;

public sealed class TerminalPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void SuggestionScrollingDoesNotRediscoverCommands()
    {
        using var workspace = new TestWorkspace();
        var discoveries = 0;
        var editor = new InputEditor(Path.Combine(workspace.Root, "history.json"), workspace.Root,
            () => { discoveries++; return CommandCatalog.Builtins; });
        var state = new InputState(); state.Set("/");
        var hints = editor.Hints(state);
        for (var i = 0; i < 1000; i++)
        {
            state.MoveSuggestion(i % 2 == 0 ? 1 : -1, hints.Count);
            Assert.Same(hints, editor.Hints(state));
        }
        Assert.Equal(1, discoveries);
        state.Insert("mod");
        Assert.Contains(editor.Hints(state), hint => hint.Text == "/model");
        Assert.Equal(1, discoveries);
        state.SelectAll(); Assert.Empty(editor.Hints(state));
    }

    [Fact]
    public void DeltaDetectsStylesAndPreservesCursorControls()
    {
        List<List<Segment>> before = [[new Segment("中文", new Style(Color.Green))], [new Segment("same")]];
        List<List<Segment>> after = [[new Segment("中文", new Style(Color.Red))], [new Segment("same")]];
        Assert.Empty(SpectreScreenDisplay.ChangedRows(before, before));
        var delta = SpectreScreenDisplay.ChangedRows(after, before);
        Assert.Equal(2, delta.Count);
        Assert.True(delta[0].IsControlCode); Assert.Equal("\u001b[1;1H", delta[0].Text);
        var frame = new SegmentFrame(delta, 80);
        var console = new TestConsole();
        var rendered = frame.Render(RenderOptions.Create(console, console.Profile.Capabilities), 80).ToArray();
        Assert.True(rendered[0].IsControlCode);
        Assert.Equal(Color.Red, rendered[1].Style.Foreground);
        Assert.Equal(4, rendered[1].CellCount());
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public async Task SlashSelectionOnlyWritesChangedRows(int width, int height)
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var skills = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, skills, mcp, new TerminalUi(false, false), console);
        screen.Append(new Text(string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"历史消息 {i}"))));
        var input = new InputState(); input.Set("/");
        var hints = input.Suggestions(CommandCatalog.Builtins);
        screen.SetInput(input, hints, "ready");
        var before = Segment.SplitLines(screen.RenderFrame(width, height), width, height);
        input.MoveSuggestion(1, hints.Count);
        var segments = screen.RenderFrame(width, height);
        var after = Segment.SplitLines(segments, width, height);
        var delta = SpectreScreenDisplay.ChangedRows(after, before);
        Assert.Equal(2, delta.Count(segment => segment.IsControlCode));
        Assert.DoesNotContain(delta, segment => segment.Text.Contains("历史消息"));
        var fullCells = segments.Sum(segment => segment.CellCount());
        var deltaCells = delta.Sum(segment => segment.CellCount());
        Assert.True(deltaCells < fullCells / 5);
        output.WriteLine($"{width}x{height}: {fullCells} -> {deltaCells} cells ({100.0 * (1 - (double)deltaCells / fullCells):F1}% reduction)");
        // Re-entry/resize requires a complete repaint, including all blank padding rows.
        Assert.Equal(height, SpectreScreenDisplay.ChangedRows(after, null).Count(segment => segment.IsControlCode));
    }

    [Fact]
    public void SafeTextFastPathStillRemovesTerminalEscapeSequences()
    {
        const string safe = "中文 👩‍💻\trow\nnext";
        Assert.Same(safe, TerminalSafeText.Clean(safe));
        Assert.Equal("safe", TerminalSafeText.Clean("\u001b[2Jsafe\u0000\u0085"));
    }
}
