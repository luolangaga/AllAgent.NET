using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanAgent.Core.Tools;

internal static class ToolResultBudget
{
    // Reserve room for exact truncation metadata outside data, so data keeps its schema.
    internal static ToolResult Apply(ToolResult result, int maxCharacters, int streamCharacters)
    {
        var original = result.ToJson();
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
        if (!changed) return result;
        var retained = Json(root);
        var normalized = result with { Data = root["data"]?.Deserialize<JsonElement>(), Error = root["error"]?.GetValue<string>(), Truncated = true };
        return normalized with { OutputTruncation = new(original.Length, retained.Length, original.Length - retained.Length) };
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
