using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>One physical row, even for streamed multiline reasoning and wide Unicode characters.</summary>
internal sealed class ThinkingLine(string thought, long elapsedMilliseconds, string prefix = "思考 › ") : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = TerminalSafeText.Clean(thought).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var suffix = $" ({lines.Length} 行)";
        var suffixWidth = new Segment(suffix).CellCount();
        var prefixWidth = new Segment(prefix).CellCount();
        var width = maxWidth - prefixWidth - suffixWidth;
        if (width < 1) return Segment.Truncate([new Segment(suffix.TrimStart(), new Style(Color.Grey))], maxWidth);
        var text = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Replace('\t', ' ') ?? "";
        var elements = StringInfo.ParseCombiningCharacters(text).Select(index => StringInfo.GetNextTextElement(text, index)).ToArray();
        var cells = elements.Select(element => new Segment(element).CellCount()).ToArray();
        var overflow = Math.Max(0, cells.Sum() - width);
        var skip = overflow == 0 ? 0 : (int)(elapsedMilliseconds / 140 % (overflow + width / 2 + 1));
        skip = Math.Min(skip, overflow);
        var start = 0; var skipped = 0;
        while (start < elements.Length && skipped < skip) skipped += cells[start++];
        var visible = ""; var count = 0;
        while (start < elements.Length && count + cells[start] <= width) { count += cells[start]; visible += elements[start++]; }
        return [new Segment(prefix + visible + new string(' ', width - count) + suffix, new Style(Color.Grey))];
    }
}
