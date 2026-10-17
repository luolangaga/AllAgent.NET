using AllAgent.NET.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class InspectorViewportTests
{
    [Fact]
    public void ScrollingReusesLayoutAndResizeInvalidatesIt()
    {
        var content = new CountingContent();
        var view = new TextViewport(content, 5, 0);
        var console = new TestConsole();
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        view.Render(options, 40).ToArray();
        for (var i = 0; i < 50; i++) { view.Offset = i; view.Render(options, 40).ToArray(); }
        Assert.Equal(1, content.Renders);
        Assert.Equal(25, view.Offset);
        view.Render(options, 30).ToArray();
        Assert.Equal(2, content.Renders);
    }

    [Fact]
    public void SelectionSpansColumnsAndRowsOutsideVisibleViewport()
    {
        var view = new TextViewport(new Text("first │ column\nsecond │ 中文\nthird │ end"), 1, 2);
        var console = new TestConsole();
        view.SelectionStart = (0, 0); view.SelectionEnd = (39, 2);
        view.SelectionColor = Color.Blue;
        var rendered = view.Render(RenderOptions.Create(console, console.Profile.Capabilities), 40).ToArray();
        Assert.Contains(rendered, s => s.Style.Background == Color.Blue);
        var text = new SelectionRegion(0, 0, 40, view.Lines.Count).Extract(view.Lines, (0, 0), (39, 2));
        Assert.Contains("first │ column", text);
        Assert.Contains("second │ 中文", text);
        Assert.Contains("third │ end", text);
    }

    [Fact]
    public async Task DragAtEdgeScrollsWhilePointerIsStationaryAndReleaseStopsIt()
    {
        using var workspace = new TestWorkspace();
        using var store = new AllAgent.NET.Core.Data.DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new AllAgent.NET.Core.Services.SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new AllAgent.NET.Core.Services.McpToolService(store, new AllAgent.NET.Core.Services.SkillExecutor());
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), new TestConsole());
        screen.InspectThought(string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line-{i:D3}")));
        screen.RenderFrame(80, 24);
        screen.InspectorInput(new(default, Pointer: new(3, 5, PointerAction.Down)));
        screen.InspectorInput(new(default, Pointer: new(3, 18, PointerAction.Drag)));
        for (var i = 1; i <= 30; i++) screen.AutoScrollDetail(i * 60);
        string Frame() => string.Concat(screen.RenderFrame(80, 24).Select(s => s.Text));
        var scrolled = Frame();
        Assert.Contains("line-030", scrolled);
        screen.InspectorInput(new(default, Pointer: new(3, 18, PointerAction.Up)));
        screen.AutoScrollDetail(10000);
        Assert.Equal(scrolled, Frame());
    }

    private sealed class CountingContent : IRenderable
    {
        public int Renders { get; private set; }
        public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            Renders++;
            for (var i = 0; i < 30; i++) { yield return new Segment($"row {i}"); if (i < 29) yield return Segment.LineBreak; }
        }
    }
}
