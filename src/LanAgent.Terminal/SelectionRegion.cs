using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

internal readonly record struct SelectionRegion(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public (int X, int Y) Clamp(int x, int y) => (Math.Clamp(x, Left, Math.Max(Left, Right - 1)), Math.Clamp(y, Top, Math.Max(Top, Bottom - 1)));
    public bool Selected(int x, int y, int cells, (int X, int Y) first, (int X, int Y) last)
    {
        if (first.Y > last.Y || first.Y == last.Y && first.X > last.X) (first, last) = (last, first);
        return y >= Top && y < Bottom && y >= first.Y && y <= last.Y && x < Math.Min(Right, y == last.Y ? last.X + 1 : Right)
            && x + cells > Math.Max(Left, y == first.Y ? first.X : Left);
    }
    public string Extract(IReadOnlyList<List<Segment>> lines, (int X, int Y) first, (int X, int Y) last)
    {
        if (first.Y > last.Y || first.Y == last.Y && first.X > last.X) (first, last) = (last, first);
        var result = new List<string>();
        for (var y = Math.Max(Top, first.Y); y <= last.Y && y < Bottom && y < lines.Count; y++)
        {
            var x = 0; var text = new StringBuilder();
            foreach (var segment in lines[y])
            {
                var elements = StringInfo.GetTextElementEnumerator(segment.Text);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement(); var cells = new Segment(element).CellCount();
                    if (cells > 0 && Selected(x, y, cells, first, last)) text.Append(element);
                    x += cells;
                }
            }
            var line = text.ToString().TrimEnd();
            // Layout borders and table separators are presentation, not copied content.
            if (line.Length > 0 && line.All(c => char.IsWhiteSpace(c) || c is >= '\u2500' and <= '\u257f')) continue;
            result.Add(line);
        }
        return string.Join(Environment.NewLine, result).TrimEnd();
    }
}
