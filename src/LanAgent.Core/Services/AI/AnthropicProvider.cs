using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public class AnthropicProvider : IAiProvider
{
    public string ProviderId => "anthropic";

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        request = request with { Config = ProviderCatalog.ResolveCredentials(request.Config) };
        using var http = ModelHttpClient.Create();
        http.DefaultRequestHeaders.Add("x-api-key", request.Config.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = Body(request);
        ReasoningSettings.Apply(body, request.Config);
        var baseUrl = request.Config.BaseUrl.TrimEnd('/');
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + (baseUrl.EndsWith("/v1") ? "/messages" : "/v1/messages"))
        {
            Content = JsonContent.Create(body)
        };

        HttpResponseMessage response;
        string? earlyError = null;
        try
        {
            response = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            earlyError = ex.Message;
            response = null!;
        }

        if (earlyError != null) { yield return new StreamChunk(ChunkType.Error, ErrorMessage: earlyError); yield break; }

        using var responseLifetime = response;

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: $"HTTP {(int)response.StatusCode}: {err}");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? eventType = null;
        var toolCallBuilders = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        var nativeBlocks = new SortedDictionary<int, JsonObject>();
        string? finishReason = null;
        var inputTokens = 0;
        var cachedInputTokens = 0;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) continue;

            if (line.StartsWith("event: "))
            {
                eventType = line["event: ".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }

            switch (eventType)
            {
                case "message_start":
                    if (node?["message"]?["usage"] is JsonObject usage)
                    {
                        inputTokens = (usage["input_tokens"]?.GetValue<int>() ?? 0) + (usage["cache_read_input_tokens"]?.GetValue<int>() ?? 0) + (usage["cache_creation_input_tokens"]?.GetValue<int>() ?? 0);
                        cachedInputTokens = usage["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
                        yield return new StreamChunk(ChunkType.Usage) { Usage = new(inputTokens, usage["output_tokens"]?.GetValue<int>() ?? 0) { CachedInputTokens = cachedInputTokens } };
                    }
                    break;
                case "content_block_start":
                {
                    var idx = node?["index"]?.GetValue<int>() ?? 0;
                    var block = node?["content_block"];
                    if (block is JsonObject native) nativeBlocks[idx] = (JsonObject)native.DeepClone();
                    if (block?["type"]?.GetValue<string>() == "tool_use")
                    {
                        var id = block["id"]?.GetValue<string>() ?? "";
                        var name = block["name"]?.GetValue<string>() ?? "";
                        toolCallBuilders[idx] = (id, name, new System.Text.StringBuilder());
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var idx = node?["index"]?.GetValue<int>() ?? 0;
                    var delta = node?["delta"];
                    var deltaType = delta?["type"]?.GetValue<string>();

                    if (deltaType == "text_delta")
                    {
                        var text = delta?["text"]?.GetValue<string>() ?? "";
                        if (nativeBlocks.TryGetValue(idx, out var native)) native["text"] = (native["text"]?.GetValue<string>() ?? "") + text;
                        if (!string.IsNullOrEmpty(text))
                            yield return new StreamChunk(ChunkType.TextDelta, TextDelta: text);
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thought = delta?["thinking"]?.GetValue<string>() ?? "";
                        if (nativeBlocks.TryGetValue(idx, out var native)) native["thinking"] = (native["thinking"]?.GetValue<string>() ?? "") + thought;
                        yield return new StreamChunk(ChunkType.ThinkingDelta, TextDelta: thought);
                    }
                    else if (deltaType == "signature_delta")
                    {
                        if (nativeBlocks.TryGetValue(idx, out var native)) native["signature"] = (native["signature"]?.GetValue<string>() ?? "") + (delta?["signature"]?.GetValue<string>() ?? "");
                    }
                    else if (deltaType == "input_json_delta" && toolCallBuilders.ContainsKey(idx))
                    {
                        var partial = delta?["partial_json"]?.GetValue<string>() ?? "";
                        toolCallBuilders[idx].Args.Append(partial);
                        yield return new StreamChunk(ChunkType.ToolCallDelta,
                            ToolCall: new ToolCallChunk(toolCallBuilders[idx].Id, toolCallBuilders[idx].Name, partial));
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var idx = node?["index"]?.GetValue<int>() ?? 0;
                    if (toolCallBuilders.TryGetValue(idx, out var tc))
                    {
                        JsonNode? input = null;
                        try { input = JsonNode.Parse(tc.Args.Length == 0 ? "{}" : tc.Args.ToString()); } catch (JsonException) { /* Output may be truncated; wait for stop_reason. */ }
                        if (nativeBlocks.TryGetValue(idx, out var native)) native["input"] = input;
                        yield return new StreamChunk(ChunkType.ToolCallComplete,
                            ToolCall: new ToolCallChunk(tc.Id, tc.Name, tc.Args.Length == 0 ? "{}" : tc.Args.ToString()));
                        toolCallBuilders.Remove(idx);
                    }
                    break;
                }
                case "error":
                    yield return new StreamChunk(ChunkType.Error, ErrorMessage: node?["error"]?["message"]?.GetValue<string>() ?? "Anthropic stream error."); yield break;
                case "message_delta":
                    finishReason = node?["delta"]?["stop_reason"]?.GetValue<string>();
                    if (node?["usage"] is JsonObject finalUsage) yield return new StreamChunk(ChunkType.Usage) { Usage = new(inputTokens, finalUsage["output_tokens"]?.GetValue<int>() ?? 0) { CachedInputTokens = finalUsage["cache_read_input_tokens"]?.GetValue<int>() ?? cachedInputTokens } };
                    break;
                case "message_stop":
                    yield return new StreamChunk(ChunkType.ProviderState, ProviderStateJson: new JsonObject { ["protocol"] = "anthropic", ["content"] = new JsonArray(nativeBlocks.Values.Select(b => (JsonNode)b).ToArray()) }.ToJsonString());
                    yield return new StreamChunk(ChunkType.Done, FinishReason: finishReason is "end_turn" or null ? "stop" : finishReason);
                    yield break;
            }
        }
        ct.ThrowIfCancellationRequested();
        yield return new StreamChunk(ChunkType.Error, ErrorMessage: "Anthropic stream ended before message_stop.");
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        return await ModelDiscovery.ListAsync(config, ct);
    }

    internal static JsonObject Body(ChatRequest request)
    {
        var body = JsonSerializer.SerializeToNode(BuildBody(request))!.AsObject();
        // Cache the reusable system prefix separately from the growing conversation.
        // Anthropic silently ignores breakpoints below the model's minimum cache size.
        if (body["system"] is JsonArray system && system.Count > 0)
        {
            // The first block is LanAgent's reusable contract; later blocks contain project/session policy.
            if (system[0] is JsonObject firstSystem) firstSystem["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            // Changing learned context must not evict the breakpoint after stable project/session instructions.
            var stableCount = request.Messages.Where(m => m.Role == "system" && !string.IsNullOrEmpty(m.Content)).TakeWhile(m => !m.IsVolatileContext).Count();
            if (stableCount > 0 && system[stableCount - 1] is JsonObject stableSystem)
                stableSystem["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }
        else if (body["tools"] is JsonArray tools && tools.LastOrDefault() is JsonObject lastTool)
            lastTool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        var conversationBreakpoints = 0;
        if (body["messages"] is JsonArray messages)
            foreach (var message in messages.Reverse().OfType<JsonObject>())
            {
                if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                    message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
                if (message["content"] is not JsonArray content) continue;
                // Signed/redacted thinking blocks must be replayed exactly, never annotated.
                var last = content.OfType<JsonObject>().LastOrDefault(block => block["type"]?.GetValue<string>() is "text" or "tool_use" or "tool_result");
                if (last == null) continue;
                last["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                // Preserve the previous turn boundary even after a large batch of tool blocks.
                if (++conversationBreakpoints == 2) break;
            }
        return body;
    }

    private static object BuildBody(ChatRequest request)
    {
        var messages = new List<object>();
        var systemPrompt = new List<object>();

        foreach (var m in request.Messages)
        {
            if (m.Role == "system") { if (!string.IsNullOrEmpty(m.Content)) systemPrompt.Add(new { type = "text", text = m.Content }); continue; }

            if (m.Role == "tool")
            {
                messages.Add(new
                {
                    role = "user",
                    content = new[] { new { type = "tool_result", tool_use_id = m.ToolCallId, content = m.Content } }
                });
            }
            else if (m.Role == "assistant" && m.ProviderStateJson is { } state && JsonNode.Parse(state)?["protocol"]?.GetValue<string>() == "anthropic")
            {
                messages.Add(new { role = "assistant", content = JsonNode.Parse(state)!["content"] });
            }
            else if (m.ToolCallsJson != null)
            {
                // assistant message with tool_calls → Anthropic tool_use blocks
                var toolCalls = JsonSerializer.Deserialize<JsonArray>(m.ToolCallsJson);
                var contentBlocks = new List<object>();
                if (!string.IsNullOrEmpty(m.Content))
                    contentBlocks.Add(new { type = "text", text = m.Content });
                if (toolCalls != null)
                    foreach (var tc in toolCalls)
                    {
                        var fn = tc?["function"];
                        contentBlocks.Add(new
                        {
                            type = "tool_use",
                            id = tc?["id"]?.GetValue<string>() ?? "",
                            name = fn?["name"]?.GetValue<string>() ?? "",
                            input = JsonSerializer.Deserialize<object>(fn?["arguments"]?.GetValue<string>() ?? "{}")
                        });
                    }
                messages.Add(new { role = "assistant", content = contentBlocks });
            }
            else
            {
                if (m.Images.Count == 0) messages.Add(new { role = m.Role, content = m.Content });
                else
                {
                    ImageAttachment.Validate(m.Images);
                    if (m.Role != "user") throw new ArgumentException("图片仅支持用户消息。");
                    var parts = new List<object>();
                    foreach (var image in m.Images) parts.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = image.Base64 } });
                    parts.Add(new { type = "text", text = m.Content });
                    messages.Add(new { role = m.Role, content = parts });
                }
            }
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var tools = new List<object>();
            foreach (var t in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                tools.Add(new { name = t.Name, description = t.Description, input_schema = t.Parameters });

            return new
            {
                model = request.Config.DefaultModel,
                system = systemPrompt,
                messages,
                tools,
                stream = true,
                max_tokens = ModelCatalog.OutputLimit(request.Config)
            };
        }

        return new
        {
            model = request.Config.DefaultModel,
            system = systemPrompt,
            messages,
            stream = true,
            max_tokens = ModelCatalog.OutputLimit(request.Config)
        };
    }
}
