using System.Globalization;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>Shared terminal-cell geometry for rendering, mouse selection and vertical caret movement.</summary>
internal sealed class InputLayout
{
    internal sealed record Cell(string Text, int Position, int Length, int Width);
    internal sealed class Row(int start)
    {
        public List<Cell> Cells { get; } = [];
        public int Start { get; } = start;
        public int End { get; set; } = start;
        public int Column(int position) => Cells.Where(cell => cell.Position < position || cell.Length == 0 && cell.Text == "› ").Sum(cell => cell.Width);
        public int HitTest(int column)
        {
            var x = 0;
            foreach (var cell in Cells)
            {
                if (column < x + cell.Width) return cell.Position + (column - x >= (cell.Width + 1) / 2 ? cell.Length : 0);
                x += cell.Width;
            }
            return column <= 0 ? Start : End;
        }
    }
    public string Text { get; }
    public int Width { get; }
    public IReadOnlyList<Row> Rows { get; }

    public InputLayout(string text, int width, bool secret = false)
    {
        Text = text; Width = Math.Max(1, width);
        var rows = new List<Row> { new(0) };
        var prefix = Width >= 2 ? "› " : "›";
        rows[0].Cells.Add(new(prefix, 0, 0, prefix.Length));
        var column = prefix.Length;
        foreach (var index in StringInfo.ParseCombiningCharacters(text).Append(text.Length))
        {
            var element = index == text.Length ? "" : StringInfo.GetNextTextElement(text, index);
            if (element.Contains('\n'))
            {
                rows[^1].End = index;
                // A newline has a caret cell when there is room, but never creates a phantom wrapped row.
                if (column < Width) rows[^1].Cells.Add(new(" ", index, 0, 1));
                rows.Add(new(index + element.Length)); column = 0; continue;
            }
            var display = element.Length == 0 ? " " : secret ? "•" : element == "\t" ? new string(' ', Math.Min(4 - column % 4, Width)) : TerminalSafeText.Clean(element);
            var cells = new Segment(display).CellCount();
            if (column > 0 && column + cells > Width) { rows[^1].End = index; rows.Add(new(index)); column = 0; }
            rows[^1].Cells.Add(new(display, index, element.Length, cells));
            rows[^1].End = index + element.Length; column += cells;
        }
        Rows = rows;
    }

    public int CaretRow(int position)
    {
        for (var index = Rows.Count - 1; index >= 0; index--) if (Rows[index].Start <= position) return index;
        return 0;
    }
}
