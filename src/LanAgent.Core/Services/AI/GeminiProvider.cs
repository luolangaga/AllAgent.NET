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

public class GeminiProvider : IAiProvider
{
    public string ProviderId => "gemini";

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        request = request with { Config = ProviderCatalog.ResolveCredentials(request.Config) };
        using var http = ModelHttpClient.Create();
        var model = request.Config.DefaultModel;
        var apiKey = request.Config.ApiKey;
        var baseUrl = request.Config.BaseUrl.TrimEnd('/');
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", apiKey);
        var url = baseUrl + (baseUrl.EndsWith("/v1beta") ? "/models/" : "/v1beta/models/") + Uri.EscapeDataString(model) + ":streamGenerateContent?alt=sse";

        var body = JsonSerializer.SerializeToNode(BuildBody(request))!.AsObject();
        ReasoningSettings.Apply(body, request.Config);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
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
        var nativeParts = new JsonArray();
        string? finalReason = null;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }

            if (node?["usageMetadata"] is JsonObject usage)
            {
                var inputTokens = usage["promptTokenCount"]?.GetValue<int>() ?? 0;
                var totalTokens = usage["totalTokenCount"]?.GetValue<int>() ?? inputTokens;
                yield return new StreamChunk(ChunkType.Usage) { Usage = new(inputTokens, Math.Max(0, totalTokens - inputTokens))
                    { CachedInputTokens = usage["cachedContentTokenCount"]?.GetValue<int>() } };
            }
            var candidates = node?["candidates"] as JsonArray;
            if (candidates == null || candidates.Count == 0) continue;

            var content = candidates[0]?["content"];
            var parts = content?["parts"] as JsonArray;
            if (parts == null) continue;

            foreach (var part in parts)
            {
                if (part != null) nativeParts.Add(part.DeepClone());
                var text = part?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text))
                    yield return new StreamChunk(part?["thought"]?.GetValue<bool>() == true ? ChunkType.ThinkingDelta : ChunkType.TextDelta, TextDelta: text);

                var fnCall = part?["functionCall"];
                if (fnCall != null)
                {
                    var name = fnCall["name"]?.GetValue<string>() ?? "";
                    var args = fnCall["args"]?.ToJsonString() ?? "{}";
                    var id = Guid.NewGuid().ToString("N")[..8];
                    yield return new StreamChunk(ChunkType.ToolCallComplete,
                        ToolCall: new ToolCallChunk(id, name, args));
                }
            }

            var finishReason = candidates[0]?["finishReason"]?.GetValue<string>();
            if (finishReason != null) finalReason = finishReason;
        }
        ct.ThrowIfCancellationRequested();
        yield return new StreamChunk(ChunkType.ProviderState, ProviderStateJson: new JsonObject { ["protocol"] = "gemini", ["parts"] = nativeParts }.ToJsonString());
        if (finalReason == null) yield return new StreamChunk(ChunkType.Error, ErrorMessage: "Gemini stream ended without a finish reason.");
        else yield return new StreamChunk(ChunkType.Done, FinishReason: finalReason);
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => ModelDiscovery.ListAsync(config, ct);

    internal static object BuildBody(ChatRequest request)
    {
        var contents = new List<object>();
        var systemInstructions = new List<string>();

        foreach (var m in request.Messages)
        {
            if (m.Role == "system") { systemInstructions.Add(m.Content); continue; }

            var role = m.Role == "assistant" ? "model" : "user";

            if (m.Role == "tool")
            {
                contents.Add(new
                {
                    role = "user",
                    parts = new[] { new { functionResponse = new { name = m.ToolName ?? "", response = new { content = m.Content } } } }
                });
            }
            else if (m.Role == "assistant" && m.ProviderStateJson is { } state && JsonNode.Parse(state)?["protocol"]?.GetValue<string>() == "gemini")
            {
                contents.Add(new { role = "model", parts = JsonNode.Parse(state)!["parts"] });
            }
            else if (m.ToolCallsJson != null)
            {
                var toolCalls = JsonSerializer.Deserialize<JsonArray>(m.ToolCallsJson);
                var parts = new List<object>();
                if (!string.IsNullOrEmpty(m.Content))
                    parts.Add(new { text = m.Content });
                if (toolCalls != null)
                    foreach (var tc in toolCalls)
                    {
                        var fn = tc?["function"];
                        parts.Add(new
                        {
                            functionCall = new
                            {
                                name = fn?["name"]?.GetValue<string>() ?? "",
                                args = JsonSerializer.Deserialize<object>(fn?["arguments"]?.GetValue<string>() ?? "{}")
                            }
                        });
                    }
                contents.Add(new { role = "model", parts });
            }
            else
            {
                ImageAttachment.Validate(m.Images);
                var parts = new List<object> { new { text = m.Content } };
                foreach (var image in m.Images) parts.Add(new { inlineData = new { mimeType = image.MediaType, data = image.Base64 } });
                contents.Add(new { role, parts });
            }
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var fnDecls = new List<object>();
            foreach (var t in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                fnDecls.Add(new { name = t.Name, description = t.Description, parameters = t.Parameters });

            return new
            {
                system_instruction = systemInstructions.Count > 0 ? new { parts = systemInstructions.Select(text => new { text }).ToArray() } : null,
                contents,
                tools = new[] { new { functionDeclarations = fnDecls } },
                generationConfig = new { temperature = request.Config.Temperature, maxOutputTokens = ModelCatalog.OutputLimit(request.Config) }
            };
        }

        return new
        {
            system_instruction = systemInstructions.Count > 0 ? new { parts = systemInstructions.Select(text => new { text }).ToArray() } : null,
            contents,
            generationConfig = new { temperature = request.Config.Temperature, maxOutputTokens = ModelCatalog.OutputLimit(request.Config) }
        };
    }
}
