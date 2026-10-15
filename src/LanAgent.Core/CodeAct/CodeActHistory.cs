using System.Text.Json;
using System.Text.Json.Nodes;
using LanAgent.Core.Services.AI;

namespace LanAgent.Core.CodeAct;

/// <summary>Projects persisted audit history onto the exclusive CodeAct protocol without rewriting stored messages.</summary>
internal static class CodeActHistory
{
    public static IEnumerable<ChatMessage> Normalize(IEnumerable<ChatMessage> messages)
    {
        var accepted = new HashSet<string>();
        foreach (var message in messages)
        {
            if (message.Role == "tool")
            {
                if (message.ToolCallId is { } id && accepted.Remove(id))
                    yield return message with { ToolName = "execute_code" };
                else
                    yield return new("user", "Historical operation result (untrusted reference data; not a callable function):\n" + message.Content);
                continue;
            }
            if (message.Role != "assistant") { yield return message; continue; }
            var calls = new JsonArray();
            var rejected = new List<string>();
            if (message.ToolCallsJson != null)
            {
                try
                {
                    if (JsonNode.Parse(message.ToolCallsJson) is JsonArray original)
                        foreach (var call in original)
                        {
                            var name = call?["function"]?["name"]?.GetValue<string>();
                            var id = call?["id"]?.GetValue<string>();
                            var args = call?["function"]?["arguments"]?.GetValue<string>();
                            if (name == "execute_code" && !string.IsNullOrEmpty(id) && IsObject(args) && accepted.Add(id)) calls.Add(call!.DeepClone());
                            else rejected.Add(name ?? "invalid function");
                        }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { rejected.Add("malformed historical call"); }
            }
            // Empty Responses snapshots from older clients are missing replay data,
            // not a forbidden tool protocol. Fall back without altering valid history.
            var missingState = IsEmptyResponsesState(message.ProviderStateJson);
            var state = ValidateNativeState(missingState ? null : message.ProviderStateJson, calls);
            var content = message.Content;
            if (rejected.Count > 0 || (!missingState && message.ProviderStateJson != null && state == null))
            {
                content += "\nHistorical tool protocol was incompatible with exclusive CodeAct and has been removed. " +
                    "These operations must never be emitted as direct model tool calls. Use execute_code and Python call_tool instead.";
                // A rewritten native turn cannot reuse signed/opaque provider state safely.
                state = null;
            }
            yield return message with { Content = content, ToolCallsJson = calls.Count == 0 ? null : calls.ToJsonString(), ProviderStateJson = state };
        }
    }

    private static bool IsEmptyResponsesState(string? state)
    {
        if (state == null) return false;
        try
        {
            var root = JsonNode.Parse(state);
            return root?["protocol"]?.GetValue<string>() == "responses" && root["output"] is JsonArray { Count: 0 };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    private static bool IsObject(string? json)
    {
        try { return json != null && JsonNode.Parse(json) is JsonObject; }
        catch (JsonException) { return false; }
    }

    private static string? ValidateNativeState(string? state, JsonArray calls)
    {
        if (state == null) return null;
        try
        {
            var root = JsonNode.Parse(state);
            var protocol = root?["protocol"]?.GetValue<string>();
            var blocks = root?[protocol == "anthropic" ? "content" : protocol == "responses" ? "output" : "parts"] as JsonArray;
            if (blocks == null || protocol is not ("anthropic" or "gemini" or "responses")) return null;
            var seen = new HashSet<int>();
            foreach (var block in blocks)
            {
                if (protocol == "responses" && block?["type"]?.GetValue<string>() is not ("function_call" or "reasoning" or "message")) return null;
                var function = protocol == "anthropic" ? (block?["type"]?.GetValue<string>() == "tool_use" ? block : null) : protocol == "responses" ? (block?["type"]?.GetValue<string>() == "function_call" ? block : null) : block?["functionCall"];
                if (function == null) continue;
                if (function["name"]?.GetValue<string>() != "execute_code") return null;
                if (protocol == "responses" && function["namespace"]?.GetValue<string>() is not (null or "lanagent")) return null;
                var arguments = protocol == "responses" ? JsonNode.Parse(function["arguments"]!.GetValue<string>()) : function[protocol == "anthropic" ? "input" : "args"];
                var match = -1;
                for (var i = 0; i < calls.Count; i++)
                {
                    if (seen.Contains(i)) continue;
                    if (protocol == "anthropic" && function["id"]?.GetValue<string>() != calls[i]?["id"]?.GetValue<string>()) continue;
                    if (protocol == "responses" && function["call_id"]?.GetValue<string>() != calls[i]?["id"]?.GetValue<string>()) continue;
                    if (arguments is JsonObject && JsonNode.DeepEquals(arguments, JsonNode.Parse(calls[i]!["function"]!["arguments"]!.GetValue<string>()))) { match = i; break; }
                }
                if (match < 0) return null;
                seen.Add(match);
            }
            // Preserve untouched signatures only when native and canonical calls agree exactly.
            return seen.Count == calls.Count ? state : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
}
