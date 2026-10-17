using System.Text;

namespace AllAgent.NET.Core.Services;

/// <summary>Parse quoted data arguments without shell expansion, substitution or metacharacter interpretation.</summary>
public static class CommandLineWords
{
    public static string[] Parse(string text)
    {
        var result = new List<string>(); var current = new StringBuilder(); char quote = '\0'; var active = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '"' || text[i + 1] == '\'')) { current.Append(text[++i]); active = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else current.Append(c); active = true; continue; }
            if (c is '\'' or '"') { quote = c; active = true; continue; }
            if (char.IsWhiteSpace(c)) { if (active) { result.Add(current.ToString()); current.Clear(); active = false; } continue; }
            current.Append(c); active = true;
        }
        if (quote != '\0') throw new ArgumentException("引号未闭合。");
        if (active) result.Add(current.ToString());
        return result.ToArray();
    }
}
