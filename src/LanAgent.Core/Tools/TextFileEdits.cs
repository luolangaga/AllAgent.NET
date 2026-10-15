using System.ComponentModel;
using System.Text;

namespace LanAgent.Core.Tools;

public sealed record FileEdit(
    [property: Description("Nonempty text unique in the original file; include enough context. Must not overlap another edit.")] string old_string,
    [property: Description("Replacement text; empty deletes the matched text.")] string new_string);

// Pi-style matching: a common original snapshot, reverse-order application,
// and fuzzy normalization confined to the lines actually touched by an edit.
internal static class TextFileEdits
{
    private sealed record Replacement(int Start, int Length, string Text, int EditIndex);
    public static string NormalizeLines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    public static string LineEnding(string value)
    {
        var index = value.IndexOf('\n');
        return index > 0 && value[index - 1] == '\r' ? "\r\n" : "\n";
    }
    private static string Fuzzy(string value)
    {
        var text = string.Join("\n", value.Normalize(NormalizationForm.FormKC).Split('\n').Select(line => line.TrimEnd()));
        return string.Concat(text.Select(c => c switch
        {
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' => '"',
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
            '\u00a0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200a' or '\u202f' or '\u205f' or '\u3000' => ' ',
            _ => c
        }));
    }
    public static (string? Content, string? Error, bool Fuzzy) Apply(string original, FileEdit[] edits)
    {
        var normalized = NormalizeLines(original);
        var requests = edits.Select(e => new FileEdit(NormalizeLines(e.old_string), NormalizeLines(e.new_string))).ToArray();
        var fuzzy = requests.Any(e => !normalized.Contains(e.old_string, StringComparison.Ordinal));
        var source = fuzzy ? Fuzzy(normalized) : normalized;
        var replacements = new List<Replacement>();
        for (var i = 0; i < requests.Length; i++)
        {
            var search = fuzzy ? Fuzzy(requests[i].old_string) : requests[i].old_string;
            if (search.Length == 0) return (null, $"edits[{i}].old_string is empty after normalization; no edit applied.", fuzzy);
            var positions = new List<int>(); var count = 0;
            for (var at = source.IndexOf(search, StringComparison.Ordinal); at >= 0; at = source.IndexOf(search, at + 1, StringComparison.Ordinal))
            { count++; if (positions.Count < 10) positions.Add(at); }
            if (count != 1)
            {
                var lines = positions.Select(p => 1 + source.AsSpan(0, p).Count('\n'));
                var details = count == 0 ? "Reread the current target region with file_read; check whitespace and stale content before retrying." :
                    $"Candidate lines (first 10): {string.Join(", ", lines)}. Include surrounding text to make the match unique.";
                return (null, $"edits[{i}]: Expected exactly one match; found {count}. {details} No edit applied.", fuzzy);
            }
            // Pi checks uniqueness in normalized space even for an exact hit:
            // visually equivalent quotes/whitespace must not hide another candidate.
            if (!fuzzy)
            {
                var fuzzySource = Fuzzy(source); var fuzzySearch = Fuzzy(search);
                var fuzzyCount = 0;
                if (fuzzySearch.Length != 0)
                    for (var at = fuzzySource.IndexOf(fuzzySearch, StringComparison.Ordinal); at >= 0; at = fuzzySource.IndexOf(fuzzySearch, at + 1, StringComparison.Ordinal)) fuzzyCount++;
                if (fuzzyCount > 1) return (null, $"edits[{i}]: Expected exactly one match; found {fuzzyCount} after whitespace/Unicode normalization. Include more surrounding text; no edit applied.", false);
            }
            replacements.Add(new(positions[0], search.Length, requests[i].new_string, i));
        }
        replacements.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < replacements.Count; i++)
            if (replacements[i - 1].Start + replacements[i - 1].Length > replacements[i].Start)
                return (null, $"edits[{replacements[i - 1].EditIndex}] and edits[{replacements[i].EditIndex}] overlap. Merge them into one edit; no edit applied.", fuzzy);

        var updated = source;
        foreach (var edit in replacements.AsEnumerable().Reverse()) updated = updated[..edit.Start] + edit.Text + updated[(edit.Start + edit.Length)..];
        if (updated == source) return (null, "No changes to apply: the replacement produced identical content.", fuzzy);
        if (fuzzy)
        {
            // Overlay replacements by touched line groups, retaining untouched original
            // lines rather than writing a globally normalized file.
            var originalLines = normalized.Split('\n'); var baseLines = source.Split('\n');
            if (originalLines.Length != baseLines.Length) return (null, "Normalization changed the line count; reread and provide exact text. No edit applied.", true);
            var starts = new int[baseLines.Length + 1];
            for (var i = 0; i < baseLines.Length; i++) starts[i + 1] = starts[i] + baseLines[i].Length + (i < baseLines.Length - 1 ? 1 : 0);
            var touched = new bool[baseLines.Length];
            foreach (var edit in replacements)
                for (var i = 0; i < baseLines.Length; i++)
                    if (starts[i] < edit.Start + edit.Length && starts[i + 1] > edit.Start) touched[i] = true;
            var preserved = new StringBuilder();
            for (var i = 0; i < baseLines.Length;)
            {
                var start = i; var changed = touched[i++];
                while (i < baseLines.Length && touched[i] == changed) i++;
                if (!changed)
                {
                    for (var j = start; j < i; j++) { preserved.Append(originalLines[j]); if (j < baseLines.Length - 1) preserved.Append('\n'); }
                    continue;
                }
                var block = source[starts[start]..starts[i]];
                foreach (var edit in replacements.Where(e => e.Start >= starts[start] && e.Start < starts[i]).Reverse())
                { var offset = edit.Start - starts[start]; block = block[..offset] + edit.Text + block[(offset + edit.Length)..]; }
                preserved.Append(block);
            }
            updated = preserved.ToString();
        }
        var result = LineEnding(original) == "\r\n" ? updated.Replace("\n", "\r\n") : updated;
        return result == original ? (null, "No changes to apply: the replacement produced identical content.", fuzzy) : (result, null, fuzzy);
    }
}
