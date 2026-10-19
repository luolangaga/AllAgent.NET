namespace AllAgent.NET.Terminal;

/// <summary>One rendered diff row; the shape the tool cards already render (`{kind, oldLine, newLine, text}`).</summary>
public sealed record DiffRow(string Kind, int? OldLine, int? NewLine, string Text);

public sealed record ParsedPatch(IReadOnlyList<DiffRow> Rows, int Added, int Removed, bool Truncated, bool Binary)
{
    public static readonly ParsedPatch Empty = new([], 0, 0, false, false);
}

/// <summary>Parses `git diff` unified output into bounded line rows, and can synthesize the
/// all-added view of an untracked file. No diff library involved; git computes, this renders.</summary>
public static class UnifiedDiff
{
    public const int MaxRows = 4000;
    private const int MaxCharacters = 512 * 1024;

    public static ParsedPatch Parse(string patch)
    {
        var rows = new List<DiffRow>();
        var added = 0;
        var removed = 0;
        var truncated = false;
        var binary = false;
        var characters = 0;
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;
        foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = TryParseHunk(raw, out oldLine, out newLine);
                continue;
            }
            if (!inHunk)
            {
                if (raw.StartsWith("Binary files", StringComparison.Ordinal) || raw.StartsWith("GIT binary patch", StringComparison.Ordinal)) binary = true;
                continue;
            }
            if (raw.Length == 0) { inHunk = false; continue; }
            if (raw[0] == '\\') continue;
            var kind = raw[0] switch { ' ' => "context", '+' => "added", '-' => "removed", _ => null };
            if (kind == null) { inHunk = false; continue; }
            if (rows.Count >= MaxRows || characters > MaxCharacters) { truncated = true; break; }
            var text = raw[1..];
            characters += text.Length;
            switch (kind)
            {
                case "added": rows.Add(new DiffRow(kind, null, newLine++, text)); added++; break;
                case "removed": rows.Add(new DiffRow(kind, oldLine++, null, text)); removed++; break;
                default: rows.Add(new DiffRow(kind, oldLine++, newLine++, text)); break;
            }
        }
        return new ParsedPatch(rows, added, removed, truncated, binary);
    }

    /// <summary>The whole file as an added-only patch, for files git does not track yet.</summary>
    public static ParsedPatch ParseAdded(string content)
    {
        if (content.Length == 0) return ParsedPatch.Empty;
        var rows = new List<DiffRow>();
        var characters = 0;
        var truncated = false;
        var line = 0;
        foreach (var text in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (rows.Count >= MaxRows || characters > MaxCharacters) { truncated = true; break; }
            characters += text.Length;
            rows.Add(new DiffRow("added", null, ++line, text));
        }
        if (rows.Count > 0 && rows[^1].Text.Length == 0 && content.EndsWith('\n')) { rows.RemoveAt(rows.Count - 1); }
        return new ParsedPatch(rows, rows.Count, 0, truncated, false);
    }

    private static bool TryParseHunk(string header, out int oldLine, out int newLine)
    {
        oldLine = 0;
        newLine = 0;
        var parts = header.Split(' ');
        if (parts.Length < 3) return false;
        return TryParseRange(parts[1], '-', out oldLine) && TryParseRange(parts[2], '+', out newLine);
    }

    private static bool TryParseRange(string token, char sign, out int start)
    {
        start = 0;
        if (token.Length == 0 || token[0] != sign) return false;
        var comma = token.IndexOf(',');
        var number = comma < 0 ? token[1..] : token[1..comma];
        return int.TryParse(number, out start);
    }
}
