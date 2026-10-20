using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Qoder;

/// <summary>Streams Qoder's agent gateway through the host provider contract.</summary>
internal sealed class QoderProvider(QoderAccount account) : IAiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ProviderId => "qoder";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        account.ValidateModelConfiguration(config);
        return (await account.Models.GetAsync(ct)).Models;
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(request, ct);
        if (prepared.Error != null)
        {
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: prepared.Error);
            yield break;
        }

        var session = prepared.Session!;
        var catalog = prepared.Catalog!;
        var send = await SendAsync(request, session, catalog, ct);
        if (send.Error != null)
        {
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: send.Error) { ErrorStatusCode = send.Status };
            yield break;
        }

        using var response = send.Response!;
        Stream? content = null;
        string? streamError = null;
        try { content = await response.Content.ReadAsStreamAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { streamError = ex.Message; }
        if (streamError != null)
        {
            yield return new StreamChunk(ChunkType.Error, ErrorMessage: streamError);
            yield break;
        }

        using var reader = new StreamReader(content!, Encoding.UTF8);
        var calls = new ToolCallAccumulator();
        string? finishReason = null;
        ModelTokenUsage? usage = null;
        while (true)
        {
            var read = await ReadLineAsync(reader, ct, finishReason == null ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(5));
            if (read.TimedOut)
            {
                if (finishReason != null) break;
                yield return new StreamChunk(ChunkType.Error, ErrorMessage: "Qoder 流式响应超时，请重试。");
                yield break;
            }
            if (read.Error != null)
            {
                yield return new StreamChunk(ChunkType.Error, ErrorMessage: read.Error);
                yield break;
            }
            var line = read.Line;
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            JsonDocument envelope;
            try { envelope = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }
            using (envelope)
            {
                var root = envelope.RootElement;
                var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("statusCodeValue", out var statusElement)
                    ? ReadIntValue(statusElement) : null;
                var inner = InnerBody(root, payload);
                if (status is int code && code is not (0 or 200))
                {
                    var message = ExtractMessage(inner) ?? (string.IsNullOrWhiteSpace(inner) ? $"HTTP {code}" : inner);
                    yield return new StreamChunk(ChunkType.Error,
                        ErrorMessage: $"Qoder 上游错误（{code}）：{Truncate(message, 600)}")
                    {
                        ErrorStatusCode = IsBillingText(inner + " " + message) ? HttpStatusCode.Forbidden : null
                    };
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(inner)) continue;
                if (inner.Trim() == "[DONE]") break;

                JsonDocument contentDocument;
                try { contentDocument = JsonDocument.Parse(inner); }
                catch (JsonException) { continue; }
                using (contentDocument)
                {
                    var body = contentDocument.RootElement;
                    var (errorMessage, billing) = ReadInnerError(body);
                    if (errorMessage != null)
                    {
                        yield return new StreamChunk(ChunkType.Error,
                            ErrorMessage: "Qoder：" + Truncate(errorMessage, 600))
                        {
                            ErrorStatusCode = billing ? HttpStatusCode.Forbidden : null
                        };
                        yield break;
                    }

                    var frameUsage = ReadUsage(body);
                    if (frameUsage != null) usage = frameUsage;
                    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    {
                        if (usage != null && finishReason != null) break;
                        continue;
                    }

                    var choice = choices[0];
                    if (choice.ValueKind != JsonValueKind.Object) continue;
                    var delta = choice.TryGetProperty("delta", out var deltaElement) && deltaElement.ValueKind == JsonValueKind.Object
                        ? deltaElement : default;
                    if (delta.ValueKind == JsonValueKind.Object)
                    {
                        var text = ReadText(delta, "content");
                        if (!string.IsNullOrEmpty(text)) yield return new StreamChunk(ChunkType.TextDelta, TextDelta: text);
                        var reasoning = ReadText(delta, "reasoning_content", "reasoning");
                        if (!string.IsNullOrEmpty(reasoning)) yield return new StreamChunk(ChunkType.ThinkingDelta, TextDelta: reasoning);
                        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var call in toolCalls.EnumerateArray())
                            {
                                if (call.ValueKind != JsonValueKind.Object) continue;
                                var (key, name, fragment) = calls.Add(call);
                                yield return new StreamChunk(ChunkType.ToolCallDelta, ToolCall: new ToolCallChunk(key, name, fragment));
                            }
                        }
                        var deltaFinish = QoderProtocol.ReadString(delta, "finish_reason");
                        if (!string.IsNullOrWhiteSpace(deltaFinish)) finishReason = deltaFinish;
                    }
                    var choiceFinish = QoderProtocol.ReadString(choice, "finish_reason");
                    if (!string.IsNullOrWhiteSpace(choiceFinish)) finishReason = choiceFinish;
                    if (usage != null && finishReason != null) break;
                }
            }
        }

        if (usage != null) yield return new StreamChunk(ChunkType.Usage) { Usage = usage };
        foreach (var call in calls.Complete())
            yield return new StreamChunk(ChunkType.ToolCallComplete, ToolCall: new ToolCallChunk(call.Id, call.Name, call.Arguments));
        if (calls.HasCalls && finishReason is null or "stop") finishReason = "tool_calls";
        yield return new StreamChunk(ChunkType.Done, FinishReason: finishReason ?? "stop");
    }

    private async Task<(QoderSession? Session, QoderCatalog? Catalog, string? Error)> PrepareAsync(ChatRequest request, CancellationToken ct)
    {
        try
        {
            account.ValidateModelConfiguration(request.Config);
            var session = await account.GetSessionAsync(ct);
            var catalog = await account.Models.GetAsync(ct);
            return (session, catalog, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    private async Task<(HttpResponseMessage? Response, string? Error, HttpStatusCode? Status)> SendAsync(
        ChatRequest request, QoderSession session, QoderCatalog catalog, CancellationToken ct)
    {
        try
        {
            var model = QoderProtocol.NormalizeModel(request.Config.DefaultModel);
            var modelConfig = catalog.Config(model);
            var payload = QoderProtocol.BuildChatPayload(request, modelConfig, session);
            var encoded = QoderProtocol.EncodeBody(Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions)));
            var body = Encoding.UTF8.GetBytes(encoded);
            var uri = new Uri(QoderProtocol.ChatUrl, UriKind.Absolute);
            var headers = QoderProtocol.BuildCosyHeaders(body, uri, session);
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            message.Content.Headers.ContentType = new("application/json");
            foreach (var header in headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            message.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            message.Headers.TryAddWithoutValidation("X-Model-Key", model);
            message.Headers.TryAddWithoutValidation("X-Model-Source", QoderProtocol.ReadString(modelConfig, "source") ?? "system");

            var response = await account.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return (response, null, null);
            var status = response.StatusCode;
            var error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            return (null, $"Qoder HTTP {(int)status}: {Truncate(error, 600)}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, ex.Message, null); }
    }

    private static async Task<(string? Line, string? Error, bool TimedOut)> ReadLineAsync(StreamReader reader, CancellationToken ct, TimeSpan timeout)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readTimeout.CancelAfter(timeout);
        try { return (await reader.ReadLineAsync(readTimeout.Token), null, false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, null, true); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return (null, ex.Message, false); }
    }

    private static int? ReadIntValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static string InnerBody(JsonElement root, string raw)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("body", out var body))
        {
            if (body.ValueKind == JsonValueKind.String) return body.GetString() ?? "";
            if (body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
            return body.GetRawText();
        }
        return raw;
    }

    private static string? ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()))
                return value.GetString();
        return null;
    }

    private static string? ExtractMessage(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return null;
        try
        {
            using var document = JsonDocument.Parse(inner);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    if (error.ValueKind == JsonValueKind.String) return error.GetString();
                    if (error.ValueKind == JsonValueKind.Object) return QoderProtocol.ReadString(error, "message", "detail") ?? error.GetRawText();
                }
                return QoderProtocol.ReadString(root, "message", "msg");
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static (string? Message, bool Billing) ReadInnerError(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, false);
        if (body.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var message = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object ? QoderProtocol.ReadString(error, "message", "detail") ?? error.GetRawText()
                : error.GetRawText();
            return (string.IsNullOrWhiteSpace(message) ? "Qoder 上游返回错误。" : message, IsBillingText(message));
        }
        if (!body.TryGetProperty("choices", out _) && body.TryGetProperty("code", out _))
        {
            var message = QoderProtocol.ReadString(body, "message", "msg") ?? body.GetRawText();
            return (message, IsBillingText(body.GetRawText()));
        }
        return (null, false);
    }

    private static ModelTokenUsage? ReadUsage(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var input = QoderProtocol.ReadInt(usage, "prompt_tokens", "input_tokens");
        var output = QoderProtocol.ReadInt(usage, "completion_tokens", "output_tokens");
        if (input == null && output == null) return null;
        var cached = QoderProtocol.ReadInt(usage, "cached_tokens", "cache_read_input_tokens");
        if (cached == null && usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cached = QoderProtocol.ReadInt(details, "cached_tokens");
        return new ModelTokenUsage(input ?? 0, output ?? 0) { CachedInputTokens = cached };
    }

    private static bool IsBillingText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("pricingUrl", StringComparison.OrdinalIgnoreCase) ||
         Regex.IsMatch(text, "\"code\"\\s*:\\s*\"?(110|112|10605)\"?"));

    private static string Truncate(string? value, int limit)
    {
        value = (value ?? "").Trim();
        return value.Length > limit ? value[..limit] : value;
    }

    private sealed class ToolCallAccumulator
    {
        private sealed class Entry
        {
            internal int Index;
            internal string Id = "";
            internal string Name = "";
            internal readonly StringBuilder Arguments = new();
        }

        private readonly SortedDictionary<int, Entry> entries = [];

        internal bool HasCalls => entries.Values.Any(entry => !string.IsNullOrWhiteSpace(entry.Name));

        internal (string Key, string Name, string Delta) Add(JsonElement call)
        {
            var index = QoderProtocol.ReadInt(call, "index") ?? 0;
            if (!entries.TryGetValue(index, out var entry))
            {
                entry = new Entry { Index = index };
                entries[index] = entry;
            }
            var id = QoderProtocol.ReadString(call, "id");
            if (!string.IsNullOrWhiteSpace(id)) entry.Id = id;
            var name = "";
            var delta = "";
            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = QoderProtocol.ReadString(function, "name") ?? "";
                delta = QoderProtocol.ReadString(function, "arguments") ?? "";
                if (!string.IsNullOrWhiteSpace(name)) entry.Name = name;
                if (!string.IsNullOrEmpty(delta)) entry.Arguments.Append(delta);
            }
            var key = string.IsNullOrWhiteSpace(entry.Id) ? $"tc_{index}" : entry.Id;
            return (key, string.IsNullOrWhiteSpace(name) ? entry.Name : name, delta);
        }

        internal IEnumerable<(string Id, string Name, string Arguments)> Complete() =>
            entries.Values
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => (string.IsNullOrWhiteSpace(entry.Id) ? $"tc_{entry.Index}" : entry.Id, entry.Name, entry.Arguments.ToString()));
    }
}
