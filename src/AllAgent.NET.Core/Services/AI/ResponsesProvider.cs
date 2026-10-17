using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

/// <summary>Responses HTTP/SSE with client-side history, including ChatGPT plan usage.</summary>
public sealed class ResponsesProvider(HttpClient? http = null) : IAiProvider
{
    private static readonly HttpClient Shared = ModelHttpClient.Create(TimeSpan.FromMinutes(5));
    private readonly HttpClient _http = http ?? Shared;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Turn, string Scope), string> _routing = new();
    public void EndRoutingTurn(Guid turnId)
    {
        foreach (var key in _routing.Keys.Where(key => key.Turn == turnId)) _routing.TryRemove(key, out _);
    }
    public string ProviderId => "responses";
    // Codex model discovery is version-gated; keep this compatibility version
    // aligned with the native Codex protocol we implement, never 0.0.0.
    internal const string CodexClientVersion = "0.163.0";
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, config, config.BaseUrl.TrimEnd('/') == AllAgent.NET.Core.Extensions.ChatGptAuthentication.CodexEndpoint ? "/models?client_version=" + CodexClientVersion : "/models");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("模型目录请求失败（HTTP " + (int)response.StatusCode + "）。");
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 2000000, ct));
        var root = json.RootElement;
        if (root.TryGetProperty("models", out var models)) return models.EnumerateArray().Where(m => m.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(m => new ModelInfo(m.GetProperty("slug").GetString()!, m.TryGetProperty("display_name", out var name) ? name.GetString() ?? m.GetProperty("slug").GetString()! : m.GetProperty("slug").GetString()!, m.TryGetProperty("context_window", out var context) && context.TryGetInt32(out var capacity) ? capacity : null, m.TryGetProperty("max_output_tokens", out var output) && output.TryGetInt32(out var maximum) ? maximum : null)
            { ReasoningLevels = m.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Where(level => level.ValueKind == JsonValueKind.Object && level.TryGetProperty("effort", out _)).Select(level => level.GetProperty("effort").GetString()!).Where(level => !string.IsNullOrWhiteSpace(level)).ToArray() : null }).ToArray();
        return root.GetProperty("data").EnumerateArray().Select(m => new ModelInfo(m.GetProperty("id").GetString()!, m.GetProperty("id").GetString()!)).ToArray();
    }
    private static HttpRequestMessage Request(HttpMethod method, ApiConfig configuration, string path)
    {
        var config = ProviderCatalog.ResolveCredentials(configuration);
        var request = new HttpRequestMessage(method, config.BaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        if (config.ExtraHeadersJson is { Length: > 0 })
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(config.ExtraHeadersJson) ?? []) request.Headers.Add(pair.Key, pair.Value);
        return request;
    }
    internal static JsonObject Body(ChatRequest request)
    {
        var input = new JsonArray(); var instructions = new List<string>();
        foreach (var message in request.Messages)
        {
            if (message.Role == "system") { instructions.Add(message.Content); continue; }
            if (message.Role == "tool") { input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message.ToolCallId, ["output"] = message.Content }); continue; }
            if (message.Role == "assistant" && message.ProviderStateJson is { } state)
            {
                var native = JsonNode.Parse(state);
                if (native?["protocol"]?.GetValue<string>() == "responses" && native["context_id"]?.GetValue<string>() == request.Config.AuthenticationContextId && native["output"] is JsonArray { Count: > 0 } output)
                { foreach (var item in output) input.Add(item?.DeepClone()); continue; }
            }
            if (message.Images.Count > 0)
            {
                ImageAttachment.Validate(message.Images);
                if (message.Role != "user") throw new ArgumentException("图片仅支持用户消息。");
                var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = message.Content });
                foreach (var image in message.Images) content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image.DataUrl });
                input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            else if (!string.IsNullOrEmpty(message.Content)) input.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
            if (message.Role == "assistant" && message.ToolCallsJson is { } calls)
                foreach (var call in JsonNode.Parse(calls)!.AsArray()) input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call!["id"]!.GetValue<string>(), ["name"] = call["function"]!["name"]!.GetValue<string>(), ["namespace"] = "allagent", ["arguments"] = call["function"]!["arguments"]!.GetValue<string>() });
        }
        var body = new JsonObject { ["model"] = request.Config.DefaultModel, ["input"] = input, ["stream"] = true, ["store"] = false, ["include"] = new JsonArray("reasoning.encrypted_content") };
        if (request.Config.ProviderId != "codex") body["max_output_tokens"] = ModelCatalog.OutputLimit(request.Config);
        if (instructions.Count > 0 || request.Config.BaseUrl.TrimEnd('/') == AllAgent.NET.Core.Extensions.ChatGptAuthentication.CodexEndpoint) body["instructions"] = string.Join("\n\n", instructions);
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal)) tools.Add(new JsonObject { ["type"] = "function", ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = JsonSerializer.SerializeToNode(tool.Parameters), ["strict"] = false });
            body["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = "allagent", ["description"] = "AllAgent's host-controlled CodeAct execution capability.", ["tools"] = tools });
            body["parallel_tool_calls"] = false;
        }
        PromptCacheRouting.Apply(body, request);
        PromptCacheRouting.ApplyRetention(body, request);
        ReasoningSettings.Apply(body, request.Config);
        return body;
    }
    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var message = Request(HttpMethod.Post, request.Config, "/responses");
        // ChatGPT cache affinity is session-scoped, independently of turn routing.
        // Preserve explicitly configured headers for callers managing their own sessions.
        if (request.Config.ProviderId == "codex" && request.ConversationId is { } session && !message.Headers.Contains("session-id"))
            message.Headers.TryAddWithoutValidation("session-id", session.ToString());
        var routeKey = request.RoutingTurnId is { } turn
            ? ((Guid Turn, string Scope)?)(turn, System.Text.Json.JsonSerializer.Serialize(new[] { request.Config.Id.ToString(), request.Config.BaseUrl, request.Config.DefaultModel, request.Config.AuthenticationContextId,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(request.Config.ApiKey ?? ""))) }))
            : null;
        if (routeKey is { } route && _routing.TryGetValue(route, out var state)) message.Headers.TryAddWithoutValidation("x-codex-turn-state", state);
        message.Headers.Accept.Add(new("text/event-stream")); message.Content = new StringContent(Body(request).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode && routeKey is { } routing && response.Headers.TryGetValues("x-codex-turn-state", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Contains('\r') && !value.Contains('\n')) _routing.TryAdd(routing, value);
        }
        if (!response.IsSuccessStatusCode)
        {
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: "Responses 请求失败（HTTP " + (int)response.StatusCode + "）。") { ErrorStatusCode = response.StatusCode };
            yield break;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
        var calls = new Dictionary<string, (string Id, string Name, StringBuilder Args)>();
        var streamedOutput = new SortedDictionary<int, JsonObject>();
        var reasoningText = new Dictionary<string, string>();
        var finishedReasoning = new HashSet<string>();
        var eventData = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 2000000) throw new InvalidDataException("Responses 事件超过大小限制。");
            if (line.StartsWith("data:", StringComparison.Ordinal)) { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(line[5..].TrimStart()); if (eventData.Length > 2000000) throw new InvalidDataException("Responses 事件过大。"); continue; }
            if (line.Length != 0 || eventData.Length == 0) continue;
            var data = eventData.ToString(); eventData.Clear();
            if (data == "[DONE]") break;
            var node = JsonNode.Parse(data) ?? throw new InvalidDataException("无效的 Responses 事件。");
            switch (node["type"]?.GetValue<string>())
            {
                case "response.output_text.delta":
                    if (FindStreamedItem(node) is { } textItem)
                    {
                        var content = textItem["content"] as JsonArray;
                        if (content == null) { content = new JsonArray(); textItem["content"] = content; }
                        var index = node["content_index"]?.GetValue<int>() ?? 0;
                        if (index is < 0 or >= 100) throw new InvalidDataException("Responses 内容索引无效。");
                        while (content.Count <= index) content.Add(new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() });
                        content[index]!["text"] = (content[index]!["text"]?.GetValue<string>() ?? "") + node["delta"]?.GetValue<string>();
                        CheckItemSize(textItem);
                    }
                    yield return new(ChunkType.TextDelta, TextDelta: node["delta"]?.GetValue<string>()); break;
                case "response.reasoning_summary_text.delta": case "response.reasoning_text.delta":
                    var key = ReasoningKey(node, node["type"]!.GetValue<string>().Contains("summary"));
                    var text = node["delta"]?.GetValue<string>() ?? "";
                    reasoningText[key] = reasoningText.GetValueOrDefault(key, "") + text;
                    if (text.Length > 0) yield return new(ChunkType.ThinkingDelta, TextDelta: text);
                    break;
                case "response.reasoning_summary_text.done": case "response.reasoning_text.done":
                case "response.reasoning_summary_part.done":
                    foreach (var remaining in CompleteReasoning(ReasoningKey(node, node["type"]!.GetValue<string>().Contains("summary")),
                        node["text"]?.GetValue<string>() ?? node["part"]?["text"]?.GetValue<string>() ?? ""))
                        yield return new(ChunkType.ThinkingDelta, TextDelta: remaining);
                    break;
                case "response.content_part.added":
                case "response.content_part.done":
                    if (FindStreamedItem(node) is { } messageItem && node["part"] is JsonObject part)
                    {
                        var content = messageItem["content"] as JsonArray;
                        if (content == null) { content = new JsonArray(); messageItem["content"] = content; }
                        var index = node["content_index"]?.GetValue<int>() ?? 0;
                        if (index is < 0 or >= 100) throw new InvalidDataException("Responses 内容索引无效。");
                        while (content.Count <= index) content.Add(null);
                        content[index] = part.DeepClone(); CheckItemSize(messageItem);
                    }
                    break;
                case "response.output_item.added":
                    if (node["item"] is JsonObject added) RememberItem(node, added);
                    if (node["item"] is JsonObject item && item["type"]?.GetValue<string>() == "function_call")
                    {
                        var name = FunctionName(item); var id = item["call_id"]!.GetValue<string>();
                        if (calls.Count >= 100) throw new InvalidDataException("Responses 函数调用数量过多。");
                        calls[item["id"]!.GetValue<string>()] = (id, name, new StringBuilder());
                        yield return new(ChunkType.ToolCallDelta, ToolCall: new(id, name, ""));
                    }
                    break;
                case "response.output_item.done":
                    if (node["item"] is not JsonObject finished) throw new InvalidDataException("Responses 完成输出项缺失。");
                    RememberItem(node, finished);
                    foreach (var remaining in ItemReasoning(finished, node["output_index"]?.GetValue<int>() ?? 0))
                        yield return new(ChunkType.ThinkingDelta, TextDelta: remaining);
                    break;
                case "response.function_call_arguments.delta":
                    if (calls.TryGetValue(node["item_id"]!.GetValue<string>(), out var call))
                    {
                        var delta = node["delta"]!.GetValue<string>(); call.Args.Append(delta);
                        if (call.Args.Length > 200000) throw new InvalidDataException("函数参数超过大小限制。");
                        if (FindStreamedItem(node) is { } function) function["arguments"] = call.Args.ToString();
                        yield return new(ChunkType.ToolCallDelta, ToolCall: new(call.Id, call.Name, delta));
                    }
                    break;
                case "response.function_call_arguments.done":
                    if (FindStreamedItem(node) is { } completedFunction) completedFunction["arguments"] = node["arguments"]?.GetValue<string>();
                    break;
                case "response.completed":
                    if (node["response"]?["status"]?.GetValue<string>() != "completed") { yield return new(ChunkType.Error, ErrorMessage: "Responses 未确认完成。"); yield break; }
                    if (node["response"]?["usage"] is JsonObject usage)
                        yield return new StreamChunk(ChunkType.Usage) { Usage = new(usage["input_tokens"]?.GetValue<int>() ?? 0, usage["output_tokens"]?.GetValue<int>() ?? 0)
                            { CachedInputTokens = usage["input_tokens_details"]?["cached_tokens"]?.GetValue<int>(), CacheWriteInputTokens = usage["input_tokens_details"]?["cache_write_tokens"]?.GetValue<int>() } };
                    if (node["response"]?["output"] is not JsonArray output) throw new InvalidDataException("Responses 完成事件缺少输出。");
                    output = CompleteOutput(output, streamedOutput);
                    for (var outputIndex = 0; outputIndex < output.Count; outputIndex++)
                        if (output[outputIndex] is JsonObject reasoningItem)
                            foreach (var remaining in ItemReasoning(reasoningItem, outputIndex)) yield return new(ChunkType.ThinkingDelta, TextDelta: remaining);
                    if (output.Count > 100 || output.Any(i => i?["type"]?.GetValue<string>() is not ("function_call" or "reasoning" or "message"))) throw new InvalidDataException("Responses 返回了未启用的输出工具或过多输出项。");
                    foreach (var completedCall in output.OfType<JsonObject>().Where(i => i["type"]?.GetValue<string>() == "function_call"))
                    {
                        var args = completedCall["arguments"]!.GetValue<string>(); if (args.Length > 200000 || JsonNode.Parse(args) is not JsonObject) throw new InvalidDataException("函数参数无效或超过大小限制。");
                        yield return new(ChunkType.ToolCallComplete, ToolCall: new(completedCall["call_id"]!.GetValue<string>(), FunctionName(completedCall), args));
                    }
                    yield return new(ChunkType.ProviderState, ProviderStateJson: new JsonObject { ["protocol"] = "responses", ["context_id"] = request.Config.AuthenticationContextId, ["output"] = output.DeepClone() }.ToJsonString());
                    yield return new(ChunkType.Done, FinishReason: output.Any(i => i?["type"]?.GetValue<string>() == "function_call") ? "tool_calls" : "stop"); yield break;
                case "response.incomplete" when (node["response"]?["incomplete_details"]?["reason"]?.GetValue<string>() == "max_output_tokens"):
                    yield return new(ChunkType.Done, FinishReason: "max_output_tokens"); yield break;
                case "response.failed": case "response.incomplete": case "error":
                    var code = node["response"]?["error"]?["code"]?.GetValue<string>() ?? node["code"]?.GetValue<string>() ?? node["type"]?.GetValue<string>();
                    yield return new(ChunkType.Error, ErrorMessage: "Responses 未完成：" + code); yield break;
            }
        }
        // Never execute partial streamed calls or treat an interrupted stream as a successful answer.
        yield return new(ChunkType.Error, ErrorMessage: "连接中断，未收到 response.completed；本轮函数调用未执行。");

        string ReasoningKey(JsonNode node, bool summary) => $"{node["output_index"]?.GetValue<int>() ?? 0}:{(summary ? "summary" : "content")}:{node[summary ? "summary_index" : "content_index"]?.GetValue<int>() ?? 0}";
        IEnumerable<string> CompleteReasoning(string key, string full)
        {
            var received = reasoningText.GetValueOrDefault(key, "");
            if (full.Length == 0 && received.Length == 0 || !finishedReasoning.Add(key)) yield break;
            if (full.StartsWith(received, StringComparison.Ordinal) && full.Length > received.Length) yield return full[received.Length..];
            if (full.Length > 0 || received.Length > 0) yield return "\n";
        }
        IEnumerable<string> ItemReasoning(JsonObject item, int index)
        {
            if (item["type"]?.GetValue<string>() != "reasoning") yield break;
            foreach (var field in new[] { "summary", "content" })
                if (item[field] is JsonArray parts)
                    for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                        if (parts[partIndex]?["text"]?.GetValue<string>() is { Length: > 0 } full)
                            foreach (var text in CompleteReasoning($"{index}:{field}:{partIndex}", full)) yield return text;
        }
        JsonObject? FindStreamedItem(JsonNode node)
        {
            if (node["output_index"] is { } index && streamedOutput.TryGetValue(index.GetValue<int>(), out var indexed)) return indexed;
            var id = node["item_id"]?.GetValue<string>();
            return id == null ? null : streamedOutput.Values.FirstOrDefault(item => item["id"]?.GetValue<string>() == id);
        }
        void RememberItem(JsonNode node, JsonObject item)
        {
            var id = item["id"]?.GetValue<string>() ?? throw new InvalidDataException("Responses 输出项缺少 ID。");
            var existing = streamedOutput.FirstOrDefault(pair => pair.Value["id"]?.GetValue<string>() == id);
            var index = node["output_index"]?.GetValue<int>() ?? (existing.Value != null ? existing.Key : streamedOutput.Count);
            if (index is < 0 or >= 100 || streamedOutput.TryGetValue(index, out var previous) && previous["id"]?.GetValue<string>() != id)
                throw new InvalidDataException("Responses 输出项索引无效。");
            CheckItemSize(item);
            streamedOutput[index] = (JsonObject)item.DeepClone();
        }
    }
    private static void CheckItemSize(JsonObject item)
    {
        if (item.ToJsonString().Length > 2000000) throw new InvalidDataException("Responses 输出项过大。");
    }
    private static JsonArray CompleteOutput(JsonArray final, SortedDictionary<int, JsonObject> streamed)
    {
        // Some streaming endpoints omit output from the final snapshot. Preserve the
        // actual ordered stream, including opaque reasoning, only after confirmed completion.
        if (streamed.Count == 0) return (JsonArray)final.DeepClone();
        var result = new JsonArray(); var used = new HashSet<JsonNode>();
        foreach (var item in streamed.Values)
        {
            var completed = final.OfType<JsonObject>().FirstOrDefault(value => value["id"]?.GetValue<string>() == item["id"]?.GetValue<string>());
            if (completed != null && (completed["type"]?.GetValue<string>() != item["type"]?.GetValue<string>() ||
                item["type"]?.GetValue<string>() == "function_call" &&
                (completed["call_id"]?.GetValue<string>() != item["call_id"]?.GetValue<string>() || FunctionName(completed) != FunctionName(item))))
                throw new InvalidDataException("Responses 最终输出与流式输出不一致。");
            if (completed != null) used.Add(completed);
            var value = (JsonObject)(completed ?? item).DeepClone();
            if (value["type"]?.GetValue<string>() is "function_call" or "message") value["status"] = "completed";
            result.Add(value);
        }
        foreach (var item in final) if (item != null && !used.Contains(item)) result.Add(item.DeepClone());
        return result;
    }
    private static string FunctionName(JsonObject item)
    {
        var name = item["name"]!.GetValue<string>(); var ns = item["namespace"]?.GetValue<string>();
        return ns is null or "allagent" ? name : ns + "." + name;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct); using var result = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (result.Length + count > limit) throw new InvalidDataException("模型目录响应过大。"); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
}
