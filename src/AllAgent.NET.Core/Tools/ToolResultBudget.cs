using System.Text.Json;
using System.Text.Json.Nodes;

namespace AllAgent.NET.Core.Tools;

internal static class ToolResultBudget
{
    // Reserve room for exact truncation metadata outside data, so data keeps its schema.
    internal static ToolResult Apply(ToolResult result, int maxCharacters, int streamCharacters)
    {
        var card = ClipCard(result.Card);
        var cardChanged = !ReferenceEquals(card, result.Card);
        var sourceResult = cardChanged ? result with { Card = card } : result;
        var original = sourceResult.ToJson();
        var root = JsonNode.Parse(original)!;
        var changed = false;
        void Streams(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if ((pair.Key.Equals("stdout", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("stderr", StringComparison.OrdinalIgnoreCase)) &&
                        pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > streamCharacters)
                    { obj[pair.Key] = Prefix(text, streamCharacters); changed = true; }
                    else Streams(pair.Value);
                }
            else if (node is JsonArray array) foreach (var item in array) Streams(item);
        }
        Streams(root["data"]);
        var budget = maxCharacters - 256;
        string Json(JsonNode node) => node.ToJsonString(ToolResult.JsonOptions);
        if (Json(root).Length > budget)
        {
            changed = true;
            var source = root["data"]?.DeepClone();
            // Find the largest uniform string allowance that fits, preserving containers and scalar fields.
            var low = 0; var high = maxCharacters;
            root["error"] = result.Error == null ? null : Prefix(result.Error, 1000);
            root["data"] = Compact(source, 0, int.MaxValue);
            if (Json(root).Length > budget)
            {
                // Even an empty-string tree is too large: retain bounded collection prefixes.
                var itemsLow = 0; var itemsHigh = maxCharacters;
                while (itemsLow < itemsHigh)
                {
                    var middle = (itemsLow + itemsHigh + 1) / 2;
                    root["data"] = Compact(source, 0, middle);
                    if (Json(root).Length <= budget) itemsLow = middle; else itemsHigh = middle - 1;
                }
                root["data"] = Compact(source, 0, itemsLow);
            }
            else
            {
                while (low < high)
                {
                    var middle = (low + high + 1) / 2;
                    root["data"] = Compact(source, middle, int.MaxValue);
                    if (Json(root).Length <= budget) low = middle; else high = middle - 1;
                }
                root["data"] = Compact(source, low, int.MaxValue);
            }
        }
        // A clipped display card alone does not make the model-facing data truncated.
        if (!changed) return cardChanged ? sourceResult : result;
        var retained = Json(root);
        var normalized = sourceResult with { Data = root["data"]?.Deserialize<JsonElement>(), Error = root["error"]?.GetValue<string>(), Truncated = true };
        return normalized with { OutputTruncation = new(original.Length, retained.Length, original.Length - retained.Length) };
    }

    // Display-only card data stays outside the model-facing data budget, so it needs its
    // own hard limits: a plugin must never turn the card into a second full result.
    private static AgentToolCard? ClipCard(AgentToolCard? card)
    {
        if (card == null) return null;
        var body = card.Body ?? "text";
        var badgeOver = card.Badge is { Length: > 64 };
        var titleOver = card.Title is { Length: > 200 };
        var summaryOver = card.Summary is { Length: > 500 };
        var bodyOver = body.Length > 16;
        var textOver = card.Text is { Length: > 4000 };
        var pathsOver = card.Paths is { Count: > 20 } || card.Paths?.Any(path => path == null || path.Length > 300) == true;
        var fieldsOver = card.Fields is { Count: > 8 } ||
            card.Fields?.Any(field => field == null || field.Label.Length > 200 || field.Value.Length > 200 || field.Tone is { Length: > 16 }) == true;
        if (!badgeOver && !titleOver && !summaryOver && !bodyOver && !textOver && !pathsOver && !fieldsOver) return card;
        return card with
        {
            Badge = card.Badge == null ? null : Prefix(card.Badge, 64),
            Title = card.Title == null ? null : Prefix(card.Title, 200),
            Summary = card.Summary == null ? null : Prefix(card.Summary, 500),
            Body = Prefix(body, 16),
            Text = card.Text == null ? null : Prefix(card.Text, 4000),
            Paths = card.Paths?.Where(path => path != null).Take(20).Select(path => Prefix(path, 300)).ToArray(),
            Fields = card.Fields?.Where(field => field != null).Take(8).Select(field => new AgentToolCardField(
                Prefix(field.Label, 200), Prefix(field.Value, 200),
                field.Tone == null ? null : Prefix(field.Tone, 16))).ToArray()
        };
    }

    private static string Prefix(string text, int length)
    {
        var end = Math.Min(text.Length, length);
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }

    private static JsonNode? Compact(JsonNode? node, int strings, int items)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(Prefix(text, strings));
        if (node is JsonArray array) return new JsonArray(array.Take(items).Select(child => Compact(child, strings, items)).ToArray());
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var pair in obj.Take(items)) copy[pair.Key] = Compact(pair.Value, strings, items);
            return copy;
        }
        return node?.DeepClone();
    }
}
