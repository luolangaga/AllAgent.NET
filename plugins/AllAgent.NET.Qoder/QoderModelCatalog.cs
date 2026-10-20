using System.Text.Json;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Qoder;

internal sealed record QoderCatalog(IReadOnlyList<ModelInfo> Models, IReadOnlyDictionary<string, JsonElement> Configs, bool Live)
{
    internal JsonElement Config(string key) => Configs.TryGetValue(key, out var value) ? value : default;
}

/// <summary>Live Qoder model list plus the exact per-model config block used for inference.</summary>
internal sealed class QoderModelCatalog(QoderAccount account)
{
    private static readonly ModelInfo[] FallbackModels =
    [
        new("auto", "Auto"),
        new("ultimate", "Ultimate"),
        new("performance", "Performance"),
        new("efficient", "Efficient"),
        new("lite", "Lite")
    ];
    private readonly SemaphoreSlim gate = new(1, 1);
    private QoderCatalog? cached;
    private DateTimeOffset expiresAt;

    internal async Task<QoderCatalog> GetAsync(CancellationToken ct)
    {
        var session = await account.GetSessionAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            if (cached != null && DateTimeOffset.UtcNow < expiresAt) return cached;
            try
            {
                cached = await FetchAsync(session, ct);
                expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A catalog outage must not block chat: fall back to the stable tier keys and
                // let the gateway apply its own defaults for the missing model config.
                cached = new QoderCatalog(FallbackModels, new Dictionary<string, JsonElement>(StringComparer.Ordinal), false);
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            return cached;
        }
        finally { gate.Release(); }
    }

    private async Task<QoderCatalog> FetchAsync(QoderSession session, CancellationToken ct)
    {
        var uri = new Uri(QoderProtocol.ModelListUrl, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in QoderProtocol.BuildCosyHeaders([], uri, session))
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var response = await account.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qoder 模型目录请求失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
        return Parse(body);
    }

    internal static QoderCatalog Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = QoderProtocol.Unwrap(document.RootElement);
        if (!root.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Array)
            throw new JsonException("Qoder 模型目录响应缺少 chat 数组。");

        var models = new List<ModelInfo>();
        var configs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in chat.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var key = QoderProtocol.ReadString(entry, "key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            configs[key] = entry.Clone();
            if (QoderProtocol.ReadBool(entry, "enable") == false) continue;
            if (!seen.Add(key)) continue;
            var display = QoderProtocol.ReadString(entry, "display_name", "displayName") ?? key;
            var reasoning = QoderProtocol.ReadBool(entry, "is_reasoning", "isReasoning") == true;
            models.Add(new ModelInfo(key, display,
                QoderProtocol.ReadInt(entry, "max_input_tokens", "maxInputTokens"),
                QoderProtocol.ReadInt(entry, "max_output_tokens", "maxOutputTokens"))
            {
                ReasoningLevels = reasoning ? ["none", "low", "medium", "high", "max"] : null
            });
        }
        if (models.Count == 0) throw new JsonException("Qoder 模型目录没有可用模型。");
        return new QoderCatalog(models, configs, true);
    }

    private static string Truncate(string value) => value.Length > 300 ? value[..300] : value;
}
