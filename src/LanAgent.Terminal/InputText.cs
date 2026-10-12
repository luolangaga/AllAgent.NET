using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>A Spectre renderable with the same cell geometry used by editing and mouse selection.</summary>
internal sealed class InputText(InputState state, bool secret = false, int maxRows = 6) : IRenderable
{
    private InputLayout.Row[] _visible = [];
    public int VisibleRows => _visible.Length;
    public int TotalRows { get; private set; }
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var layout = new InputLayout(state.Text, maxWidth, secret);
        if (!secret) state.Layout(layout);
        TotalRows = layout.Rows.Count;
        var caretRow = layout.CaretRow(state.Cursor);
        var limit = Math.Max(1, maxRows);
        var first = Math.Clamp(caretRow - limit + 1, 0, Math.Max(0, layout.Rows.Count - limit));
        _visible = layout.Rows.Skip(first).Take(limit).ToArray();
        for (var row = 0; row < _visible.Length; row++)
        {
            if (row > 0) yield return Segment.LineBreak;
            foreach (var cell in _visible[row].Cells)
            {
                var selected = cell.Length > 0 && cell.Position >= state.SelectionStart && cell.Position < state.SelectionStart + state.SelectionLength;
                var monochrome = options.ColorSystem == ColorSystem.NoColors;
                var style = cell.Length == 0 && cell.Text == "› " ? new Style(Color.Cyan1, decoration: Decoration.Bold) : Style.Plain;
                if (selected) style = new Style(Color.White, Color.Blue, decoration: monochrome ? Decoration.Invert : Decoration.None);
                if (cell.Position == state.Cursor && row + first == caretRow && cell.Text != "› ")
                    style = style.Combine(new Style(decoration: selected && monochrome ? Decoration.Underline : Decoration.Invert));
                yield return new Segment(cell.Text, style);
            }
        }
    }
    public int HitTest(int x, int y) => _visible.Length == 0 ? state.Cursor : _visible[Math.Clamp(y, 0, _visible.Length - 1)].HitTest(x);
}
