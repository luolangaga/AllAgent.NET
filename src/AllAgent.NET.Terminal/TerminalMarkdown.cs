using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

/// <summary>Small terminal-safe Markdown renderer for chat messages.</summary>
internal static class TerminalMarkdown
{
    private static readonly Regex InlinePattern = new(
        @"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<boldAlt>.+?)__|\[(?<label>[^\]]+)\]\((?<url>https?://[^\s)]+)\)|(?<!\*)\*(?<italic>[^*\r\n]+)\*(?!\*)|(?<!_)_(?<italicAlt>[^_\r\n]+)_(?!_)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrderedList = new(@"^\s*(?<number>\d+)[.)]\s+(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IRenderable Message(string label, string content, bool user)
        => new Panel(Render(content)).Header(Markup.Escape(label)).Border(BoxBorder.Heavy)
            .BorderColor(user ? FullScreenTerminal.Current?.Theme.Accent ?? Color.Cyan1 : Color.Grey).Padding(1, 0).Expand();

    public static IRenderable Render(string content)
    {
        var rows = new List<IRenderable>();
        var paragraph = new List<string>();
        var code = new List<string>();
        string? codeLanguage = null;
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            rows.Add(new Markup(Inline(string.Join(' ', paragraph)))); paragraph.Clear();
        }
        void FlushCode()
        {
            rows.Add(new Panel(new Text(string.Join('\n', code), new Style(Color.Grey))).Header(Markup.Escape(string.IsNullOrWhiteSpace(codeLanguage) ? "代码" : codeLanguage))
                .Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(1, 0));
            code.Clear(); codeLanguage = null;
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var line = 0; line < lines.Length; line++)
        {
            var raw = lines[line];
            var trimmed = raw.TrimStart();
            if (codeLanguage != null)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal)) FlushCode();
                else code.Add(raw);
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(); codeLanguage = trimmed[3..].Trim(); continue;
            }
            if (line + 1 < lines.Length && raw.Contains('|'))
            {
                var headers = TableCells(raw); var separators = TableCells(lines[line + 1]);
                if (headers.Count > 0 && headers.Count == separators.Count && separators.All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$")))
                {
                    FlushParagraph();
                    var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
                    for (var column = 0; column < headers.Count; column++)
                    {
                        var separator = separators[column];
                        var alignment = separator.EndsWith(':') ? separator.StartsWith(':') ? Justify.Center : Justify.Right : Justify.Left;
                        table.AddColumn(new TableColumn(new Markup("[bold]" + Inline(headers[column]) + "[/]")) { Alignment = alignment });
                    }
                    line++;
                    while (line + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[line + 1]) && lines[line + 1].Contains('|'))
                    {
                        var cells = TableCells(lines[++line]);
                        table.AddRow(Enumerable.Range(0, headers.Count).Select(column => (IRenderable)new Markup(Inline(cells.ElementAtOrDefault(column) ?? ""))).ToArray());
                    }
                    rows.Add(table); continue;
                }
            }
            if (string.IsNullOrWhiteSpace(raw)) { FlushParagraph(); if (rows.Count > 0 && rows[^1] is not Text) rows.Add(new Text("")); continue; }
            var heading = Regex.Match(trimmed, @"^#{1,6}\s+(.*?)(?:\s+#+)?$");
            if (heading.Success)
            {
                FlushParagraph(); rows.Add(new Markup("[bold cyan]" + Inline(heading.Groups[1].Value) + "[/]")); continue;
            }
            if (Regex.IsMatch(trimmed, @"^(?:---+|___+|\*\*\*+)\s*$")) { FlushParagraph(); rows.Add(new Rule().RuleStyle("grey")); continue; }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph(); rows.Add(new Markup("[grey]│[/] [italic]" + Inline(trimmed[2..]) + "[/]")); continue;
            }
            if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*' or '+') && char.IsWhiteSpace(trimmed[1]))
            {
                FlushParagraph(); rows.Add(new Markup("  [cyan]•[/] " + Inline(trimmed[2..].Trim()))); continue;
            }
            var ordered = OrderedList.Match(raw);
            if (ordered.Success)
            {
                FlushParagraph(); rows.Add(new Markup("  [cyan]" + ordered.Groups["number"].Value + ".[/] " + Inline(ordered.Groups["text"].Value))); continue;
            }
            paragraph.Add(raw.Trim());
        }
        if (codeLanguage != null) FlushCode();
        FlushParagraph();
        return rows.Count == 0 ? new Text("") : new Rows(rows);
    }

    private static List<string> TableCells(string row)
    {
        var cells = new List<string>(); var cell = new System.Text.StringBuilder(); var ticks = 0;
        var leadingPipe = false; var trailingPipe = false;
        row = row.Trim();
        for (var index = 0; index < row.Length; index++)
        {
            var character = row[index];
            if (character == '\\' && index + 1 < row.Length && row[index + 1] is '|' or '\\')
            { cell.Append(row[++index]); trailingPipe = false; continue; }
            if (character == '`')
            {
                var count = 1; while (index + count < row.Length && row[index + count] == '`') count++;
                if (ticks == 0) ticks = count; else if (ticks == count) ticks = 0;
                cell.Append('`', count); index += count - 1; trailingPipe = false; continue;
            }
            if (character == '|' && ticks == 0)
            {
                if (index == 0) leadingPipe = true;
                cells.Add(cell.ToString().Trim()); cell.Clear(); trailingPipe = index == row.Length - 1;
            }
            else { cell.Append(character); trailingPipe = false; }
        }
        cells.Add(cell.ToString().Trim());
        if (trailingPipe) cells.RemoveAt(cells.Count - 1);
        if (leadingPipe) cells.RemoveAt(0);
        return cells;
    }

    private static string Inline(string text)
    {
        var output = new System.Text.StringBuilder();
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            output.Append(Markup.Escape(text[position..match.Index]));
            if (match.Groups["code"].Success) output.Append("[yellow on #202020] ").Append(Markup.Escape(match.Groups["code"].Value)).Append(" [/]");
            else if (match.Groups["bold"].Success) output.Append("[bold]").Append(Inline(match.Groups["bold"].Value)).Append("[/]");
            else if (match.Groups["boldAlt"].Success) output.Append("[bold]").Append(Inline(match.Groups["boldAlt"].Value)).Append("[/]");
            else if (match.Groups["italic"].Success) output.Append("[italic]").Append(Inline(match.Groups["italic"].Value)).Append("[/]");
            else if (match.Groups["italicAlt"].Success) output.Append("[italic]").Append(Inline(match.Groups["italicAlt"].Value)).Append("[/]");
            else if (match.Groups["label"].Success)
                output.Append("[deepskyblue1]").Append(Markup.Escape(match.Groups["label"].Value)).Append("[/] [dim]").Append(Markup.Escape(match.Groups["url"].Value)).Append("[/]");
            else output.Append(Markup.Escape(match.Value));
            position = match.Index + match.Length;
        }
        output.Append(Markup.Escape(text[position..]));
        return output.ToString();
    }
}
