using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Terminal;

/// <summary>Terminal presentation for the host-neutral card a tool attaches to ToolResult.Card.</summary>
internal static class ToolCardPresentation
{
    public static AgentToolCard? Read(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.Object) return null;
            return card.Deserialize<AgentToolCard>(ToolResult.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }

    public static string Badge(AgentToolCard card, string fallback)
        => CleanLine(card.Badge) ?? fallback.ToUpperInvariant();

    public static string Title(AgentToolCard card, string fallback)
        => CleanLine(card.Title) ?? fallback;

    public static string? Summary(AgentToolCard card) => CleanLine(card.Summary);

    public static string[] Lines(AgentToolCard card)
    {
        if (string.Equals(card.Body, "paths", StringComparison.OrdinalIgnoreCase) && card.Paths is { Count: > 0 })
            return card.Paths.Select(CleanLine).Where(line => line != null).Select(line => line!).ToArray();
        if (CleanText(card.Text) is { } text)
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return Summary(card) is { } summary ? [summary] : [];
    }

    public static string Fields(AgentToolCard card) => string.Join(" · ", (card.Fields ?? Array.Empty<AgentToolCardField>())
        .Where(field => field != null)
        .Select(field => CleanLine(field.Label) + ": " + CleanLine(field.Value))
        .Where(text => text.Length > 0));

    public static string Detail(AgentToolCard card)
    {
        var text = CleanText(card.Text);
        var fields = Fields(card);
        return (text ?? "") + (fields.Length > 0 ? (text == null ? "" : "\n") + fields : "");
    }

    private static string? CleanText(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : TerminalSafeText.Clean(text).Trim();

    private static string? CleanLine(string? text)
        => CleanText(text)?.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim() is { Length: > 0 } line ? line : null;
}
