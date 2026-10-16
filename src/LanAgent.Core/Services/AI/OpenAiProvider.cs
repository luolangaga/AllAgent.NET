using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public class OpenAiProvider : IAiProvider
{
    public string ProviderId => "openai";

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = ProviderCatalog.ResolveCredentials(request.Config);
        var hasTools = request.Tools is { Count: > 0 };

        await foreach (var chunk in StreamChatRawAsync(config, request, hasTools, ct))
            yield return chunk;
    }

    private static async IAsyncEnumerable<StreamChunk> StreamChatRawAsync(
        ApiConfig config,
        ChatRequest request,
        bool hasTools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var body = BuildRawRequestBody(config, request, hasTools);

        using var http = ModelHttpClient.Create(TimeSpan.FromMinutes(5));
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        if (!string.IsNullOrEmpty(config.ExtraHeadersJson))
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, string>>(config.ExtraHeadersJson);
            if (extra != null)
                foreach (var kv in extra)
                    http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        string? sendError = null;
        try
        {
            response = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sendError = ex.Message;
            response = null!;
        }

        if (sendError is not null)
        {
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: sendError);
            yield break;
        }

        using var responseLifetime = response;

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            yield return new StreamChunk(ChunkType.Error,
                ErrorMessage: $"HTTP {(int)response.StatusCode}: {errBody}");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallAccumulator = new Dictionary<string, (string Name, StringBuilder Args)>();
        var toolIdsByIndex = new Dictionary<int, string>();
        string? finishReason = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                if (finishReason == null) { yield return new StreamChunk(ChunkType.Error, ErrorMessage: "OpenAI stream ended without a finish reason."); yield break; }
                foreach (var kv in toolCallAccumulator)
                {
                    if (string.IsNullOrWhiteSpace(kv.Value.Name))
                        continue;
                    yield return new StreamChunk(ChunkType.ToolCallComplete,
                        ToolCall: new ToolCallChunk(kv.Key, kv.Value.Name, kv.Value.Args.ToString()));
                }
                yield return new StreamChunk(ChunkType.Done, FinishReason: finishReason);
                yield break;
            }

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }

            if (node is null) continue;
            if (node["usage"] is JsonObject usage)
                yield return new StreamChunk(ChunkType.Usage) { Usage = new(usage["prompt_tokens"]?.GetValue<int>() ?? 0, usage["completion_tokens"]?.GetValue<int>() ?? 0)
                    { CachedInputTokens = usage["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() } };
            var choices = node["choices"] as JsonArray;
            if (choices is null || choices.Count == 0) continue;
            var choice = choices[0];
            finishReason = choice?["finish_reason"]?.GetValue<string>() ?? finishReason;
            var delta = choice?["delta"];
            if (delta is null) continue;

            var content = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content))
                yield return new StreamChunk(ChunkType.TextDelta, TextDelta: content);

            var reasoning = delta["reasoning_content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(reasoning) && delta["reasoning"] is JsonValue reasoningValue && reasoningValue.TryGetValue<string>(out var compatibleReasoning)) reasoning = compatibleReasoning;
            if (!string.IsNullOrEmpty(reasoning))
                yield return new StreamChunk(ChunkType.ThinkingDelta, TextDelta: reasoning);

            var toolCalls = delta["tool_calls"] as JsonArray;
            if (toolCalls is null) continue;

            foreach (var tc in toolCalls)
            {
                var index = tc?["index"]?.GetValue<int>() ?? 0;
                var id = tc?["id"]?.GetValue<string>();
                var fn = tc?["function"];
                var fnName = fn?["name"]?.GetValue<string>();
                var fnArgs = fn?["arguments"]?.GetValue<string>() ?? "";

                if (id != null) toolIdsByIndex[index] = id;
                var key = toolIdsByIndex.GetValueOrDefault(index) ?? $"tc_{index}";
                if (!toolCallAccumulator.ContainsKey(key))
                    toolCallAccumulator[key] = ("", new StringBuilder());

                if (!string.IsNullOrEmpty(fnName))
                    toolCallAccumulator[key] = (fnName, toolCallAccumulator[key].Args);

                if (!string.IsNullOrEmpty(fnArgs))
                    toolCallAccumulator[key].Args.Append(fnArgs);

                if (toolCallAccumulator[key].Args.Length > 200000)
                    throw new InvalidOperationException("Streamed tool arguments exceed the size limit.");

                yield return new StreamChunk(ChunkType.ToolCallDelta,
                    ToolCall: new ToolCallChunk(key, fnName ?? toolCallAccumulator[key].Name, fnArgs));
            }
        }

        if (finishReason == null) { yield return new StreamChunk(ChunkType.Error, ErrorMessage: "OpenAI stream ended without a finish reason; partial calls were not executed."); yield break; }
        foreach (var kv in toolCallAccumulator)
        {
            if (string.IsNullOrWhiteSpace(kv.Value.Name))
                continue;
            yield return new StreamChunk(ChunkType.ToolCallComplete,
                ToolCall: new ToolCallChunk(kv.Key, kv.Value.Name, kv.Value.Args.ToString()));
        }
        yield return new StreamChunk(ChunkType.Done, FinishReason: finishReason);
    }

    internal static string BuildRawRequestBody(ApiConfig config, ChatRequest request, bool hasTools)
    {
        var messages = new JsonArray();

        foreach (var m in request.Messages)
        {
            var msg = new JsonObject { ["role"] = m.Role };

            if (m.Role == "assistant" && !string.IsNullOrEmpty(m.ToolCallsJson))
            {
                try
                {
                    var tcs = JsonSerializer.Deserialize<JsonArray>(m.ToolCallsJson);
                    if (tcs is not null && tcs.Count > 0)
                    {
                        msg["content"] = null;
                        msg["tool_calls"] = tcs;
                    }
                    else
                    {
                        msg["content"] = m.Content ?? "";
                    }
                }
                catch
                {
                    msg["content"] = m.Content ?? "";
                }
            }
            else
            {
                msg["content"] = m.Content ?? "";
            }

            if (m.Role == "assistant" && (!string.IsNullOrWhiteSpace(m.ReasoningContent) || hasTools && ProviderCatalog.ReasoningFor(config) == "deepseek"))
                msg["reasoning_content"] = m.ReasoningContent ?? "";
            if (m.Images.Count > 0)
            {
                ImageAttachment.Validate(m.Images);
                if (m.Role != "user") throw new ArgumentException("图片仅支持用户消息。");
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var image in m.Images) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image.DataUrl } });
                msg["content"] = content;
            }

            if (m.Role == "tool")
            {
                msg["tool_call_id"] = m.ToolCallId ?? "";
                if (!string.IsNullOrEmpty(m.ToolName))
                    msg["name"] = m.ToolName;
            }

            messages.Add(msg);
        }

        var body = new JsonObject
        {
            ["model"] = config.DefaultModel,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        PromptCacheRouting.Apply(body, request);
        body["temperature"] = config.Temperature;
        body["max_tokens"] = ModelCatalog.OutputLimit(config);
        ReasoningSettings.Apply(body, config);

        if (hasTools)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools!.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var parameters = t.Parameters is JsonElement je
                    ? JsonNode.Parse(je.GetRawText())
                    : JsonNode.Parse("{}");
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = parameters,
                    }
                });
            }
            body["tools"] = tools;
        }

        LogRequest(config, body, hasTools, request.Messages.Count);
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void LogRequest(ApiConfig config, JsonObject body, bool hasTools, int msgCount)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LanAgent", "lanagent.log");
            File.AppendAllText(logPath,
                $"\n[API] Model={config.DefaultModel} Thinking={config.ThinkingEnabled} Tools={hasTools} MsgCount={msgCount}\n");
        }
        catch { }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        return await ModelDiscovery.ListAsync(config, ct);
    }
}
