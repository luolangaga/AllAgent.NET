using System.Text;
using System.Text.RegularExpressions;

namespace AllAgent.NET.CodeGraph;

/// <summary>Minimal .gitignore support for the index walker: comments, negation, directory-only
/// patterns, anchoring and the common glob forms. Deeper files are loaded later, so they win.</summary>
internal sealed class GitIgnore
{
    private static readonly GitIgnore EmptyRules = new([]);
    private readonly List<Rule> _rules;

    public static GitIgnore Empty => EmptyRules;

    private GitIgnore(List<Rule> rules) => _rules = rules;

    public static GitIgnore Load(string directory, GitIgnore inherited)
    {
        var path = Path.Combine(directory, ".gitignore");
        if (!File.Exists(path)) return inherited;
        try
        {
            List<Rule>? rules = null;
            foreach (var line in File.ReadLines(path))
            {
                if (Parse(line, directory) is not { } rule) continue;
                rules ??= new List<Rule>(inherited._rules);
                rules.Add(rule);
            }
            return rules == null ? inherited : new GitIgnore(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return inherited;
        }
    }

    public bool IsIgnored(string absolutePath, bool directory)
    {
        var decision = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !directory) continue;
            var relative = Path.GetRelativePath(rule.BaseDirectory, absolutePath).Replace('\\', '/').TrimEnd('/');
            if (relative.Length == 0 || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) continue;
            if (rule.Pattern.IsMatch(relative)) decision = !rule.Negate;
        }
        return decision;
    }

    private static Rule? Parse(string line, string baseDirectory)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#') return null;
        var negate = text[0] == '!';
        if (negate) text = text[1..];
        if (text.Length == 0) return null;
        var directoryOnly = text.EndsWith('/');
        if (directoryOnly) text = text[..^1];
        var anchored = text.StartsWith('/') || text.Contains('/');
        if (text.StartsWith('/')) text = text[1..];
        if (text.Length == 0) return null;
        var body = Glob(text);
        var pattern = anchored
            ? "^" + body + "$"
            : "(^|.*/)" + body + "$";
        try
        {
            return new Rule(baseDirectory, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), negate, directoryOnly);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Glob(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        index++;
                        if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                        {
                            index++;
                            builder.Append("(?:.*/)?");
                        }
                        else builder.Append(".*");
                    }
                    else builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    var close = pattern.IndexOf(']', index + 1);
                    if (close > index)
                    {
                        builder.Append(pattern, index, close - index + 1);
                        index = close;
                    }
                    else builder.Append("\\[");
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }
        return builder.ToString();
    }

    private sealed record Rule(string BaseDirectory, Regex Pattern, bool Negate, bool DirectoryOnly);
}
