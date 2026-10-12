namespace LanAgent.Core.Tools;

/// <summary>A bounded line diff. Very large comparison regions fall back to a replacement hunk.</summary>
public static class FileChangePreview
{
    public sealed record Line(string Kind, int? OldLine, int? NewLine, string Text);
    public static object Create(string before, string after)
    {
        static string[] Lines(string value) => value.Length == 0 ? [] : value.Replace("\r\n", "\n").Split('\n');
        var oldLines = Lines(before); var newLines = Lines(after);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var oldCount = oldLines.Length - prefix - suffix; var newCount = newLines.Length - prefix - suffix;
        var removed = 0; var added = 0;
        var lines = new List<Line>(); var characters = 0; var truncated = false;
        void Add(string kind, int? oldLine, int? newLine, string text)
        {
            if (lines.Count >= 120 || characters + text.Length > 12000) { truncated = true; return; }
            lines.Add(new(kind, oldLine, newLine, text)); characters += text.Length;
        }
        for (var i = Math.Max(0, prefix - 3); i < prefix; i++) Add("context", i + 1, i + 1, oldLines[i]);
        var replacement = (long)(oldCount + 1) * (newCount + 1) > 1000000;
        if (replacement)
        {
            removed = oldCount; added = newCount;
            for (var i = prefix; i < oldLines.Length - suffix; i++) Add("removed", i + 1, null, oldLines[i]);
            for (var i = prefix; i < newLines.Length - suffix; i++) Add("added", null, i + 1, newLines[i]);
        }
        else
        {
            var common = new int[oldCount + 1, newCount + 1];
            for (var i = oldCount - 1; i >= 0; i--)
                for (var j = newCount - 1; j >= 0; j--)
                    common[i, j] = oldLines[prefix + i] == newLines[prefix + j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            var x = 0; var y = 0;
            while (x < oldCount || y < newCount)
            {
                if (x < oldCount && y < newCount && oldLines[prefix + x] == newLines[prefix + y])
                { Add("context", prefix + x + 1, prefix + y + 1, oldLines[prefix + x]); x++; y++; }
                else if (x < oldCount && (y == newCount || common[x + 1, y] >= common[x, y + 1]))
                { Add("removed", prefix + x + 1, null, oldLines[prefix + x]); removed++; x++; }
                else { Add("added", null, prefix + y + 1, newLines[prefix + y]); added++; y++; }
            }
        }
        for (var i = 0; i < Math.Min(3, suffix); i++) Add("context", oldLines.Length - suffix + i + 1, newLines.Length - suffix + i + 1, newLines[newLines.Length - suffix + i]);
        return new { added, removed, lines, truncated, replacement };
    }
}
