using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>One physical row, even for streamed multiline reasoning and wide Unicode characters.</summary>
internal sealed class ThinkingLine(string thought, long elapsedMilliseconds, string prefix = "思考 › ", string hint = "", string emptyText = "等待模型返回思考内容…") : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var clean = TerminalSafeText.Clean(thought).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        var lines = clean.Length == 0 ? [] : clean.Split('\n');
        var suffix = $" （{lines.Length}行）" + (maxWidth >= 50 ? hint : "");
        var suffixWidth = new Segment(suffix).CellCount();
        var prefixWidth = new Segment(prefix).CellCount();
        var width = maxWidth - prefixWidth - suffixWidth;
        if (width < 1) return Segment.Truncate([new Segment(suffix.TrimStart(), new Style(Color.Grey))], maxWidth);
        var text = lines.Length == 0 ? emptyText : lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Replace('\t', ' ') ?? "";
        var elements = StringInfo.ParseCombiningCharacters(text).Select(index => StringInfo.GetNextTextElement(text, index)).ToArray();
        var cells = elements.Select(element => new Segment(element).CellCount()).ToArray();
        var overflow = Math.Max(0, cells.Sum() - width);
        var skip = overflow == 0 ? 0 : (int)(elapsedMilliseconds / 140 % (overflow + width / 2 + 1));
        skip = Math.Min(skip, overflow);
        var start = 0; var skipped = 0;
        while (start < elements.Length && skipped < skip) skipped += cells[start++];
        var visible = ""; var count = 0;
        while (start < elements.Length && count + cells[start] <= width) { count += cells[start]; visible += elements[start++]; }
        return [new Segment(prefix + visible + new string(' ', Math.Max(0, width - count)) + suffix, new Style(Color.Grey))];
    }
}
