using System.Text.Json;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

/// <summary>Models.dev snapshot plus endpoint metadata. Exact provider/model identity only.</summary>
public static class ModelCatalog
{
    private sealed record CatalogModel(string Name, int Context, int Output, bool? Reasoning = null);
    private sealed record CatalogProvider(string Name, string Api, string Protocol, Dictionary<string, CatalogModel> Models);
    private static Dictionary<string, CatalogProvider> Catalog = Load();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static DateTimeOffset NextRefresh;
    private static Dictionary<string, CatalogProvider> Load()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("LanAgent.Core.Services.AI.models-catalog.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, CatalogProvider>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    private static string CatalogId(ApiConfig config)
    {
        if (Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint))
        {
            if (endpoint.Host == "dashscope.aliyuncs.com") return "alibaba-cn";
            if (endpoint.Host == "api.moonshot.cn") return "moonshotai-cn";
            if (endpoint.Host == "api.siliconflow.cn") return "siliconflow-cn";
            if (config.ProviderId == "custom")
            {
                var match = Catalog.FirstOrDefault(p => Uri.TryCreate(p.Value.Api, UriKind.Absolute, out var api) && api.Host == endpoint.Host && api.AbsolutePath.TrimEnd('/') == endpoint.AbsolutePath.TrimEnd('/'));
                if (match.Key != null) return match.Key;
            }
        }
        return config.ProviderId switch { "gemini" => "google", "dashscope" => "alibaba", "zhipu" => "zhipuai", "codex" => "openai", "moonshot" => "moonshotai", _ => config.ProviderId };
    }
    public static IReadOnlyList<ProviderPreset> Providers => Catalog.Where(p => !string.IsNullOrWhiteSpace(p.Value.Api)).Select(p => new ProviderPreset(p.Key, p.Value.Name, p.Value.Protocol, p.Value.Api, "none")).ToArray();
    public static IReadOnlyList<ModelInfo> List(ApiConfig config) => Catalog.TryGetValue(CatalogId(config), out var provider)
        ? provider.Models.Select(m => new ModelInfo(m.Key, m.Value.Name, Positive(m.Value.Context), Positive(m.Value.Output))).ToArray() : [];

    /// <summary>Refresh public metadata only; offline failures retain the bundled snapshot.</summary>
    public static async Task RefreshAsync(CancellationToken ct = default)
    {
        await RefreshGate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < NextRefresh) return;
            using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync("https://models.dev/api.json", ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var updated = ParseLiveCatalog(json.RootElement);
            if (updated.Count == 0) throw new JsonException("Empty model catalog.");
            Catalog = updated;
            NextRefresh = DateTimeOffset.UtcNow.AddHours(24);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            NextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        finally { RefreshGate.Release(); }
    }
    private static Dictionary<string, CatalogProvider> ParseLiveCatalog(JsonElement root)
    {
        var result = new Dictionary<string, CatalogProvider>();
        foreach (var entry in root.EnumerateObject())
        {
            var value = entry.Value;
            var npm = value.TryGetProperty("npm", out var package) ? package.GetString() : null;
            var protocol = npm switch { "@ai-sdk/openai" or "@ai-sdk/openai-compatible" => "openai", "@ai-sdk/anthropic" => "anthropic", "@ai-sdk/google" => "gemini", _ => null };
            if (protocol == null || !value.TryGetProperty("models", out var models)) continue;
            var items = new Dictionary<string, CatalogModel>();
            foreach (var model in models.EnumerateObject())
            {
                if (!model.Value.TryGetProperty("limit", out var limit)) continue;
                int Read(string key) => limit.TryGetProperty(key, out var number) && number.TryGetInt32(out var n) && n > 0 ? n : 0;
                items[model.Name] = new(model.Value.TryGetProperty("name", out var name) ? name.GetString() ?? model.Name : model.Name, Read("context"), Read("output"), model.Value.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind is JsonValueKind.True or JsonValueKind.False ? reasoning.GetBoolean() : null);
            }
            result[entry.Name] = new(value.TryGetProperty("name", out var title) ? title.GetString() ?? entry.Name : entry.Name, value.TryGetProperty("api", out var api) ? api.GetString() ?? "" : "", protocol, items);
        }
        return result;
    }
    private static int? Positive(int? value) => value > 0 ? value : null;
    private static CatalogModel? FindModel(ApiConfig config)
    {
        if (Catalog.TryGetValue(CatalogId(config), out var provider) && provider.Models.TryGetValue(config.DefaultModel, out var exact)) return exact;
        // A custom proxy may expose native model IDs. Accept only an unambiguous exact ID.
        var matches = new[] { "openai", "anthropic", "google", "deepseek" }
            .Where(Catalog.ContainsKey).Select(id => Catalog[id].Models.GetValueOrDefault(config.DefaultModel))
            .Where(m => m != null).DistinctBy(m => (m!.Context, m.Output, m.Reasoning)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static bool? SupportsReasoning(ApiConfig config) => FindModel(config)?.Reasoning;
    public static ModelInfo? Resolve(ApiConfig config)
    {
        var match = FindModel(config);
        var known = match == null ? null : new ModelInfo(config.DefaultModel, match.Name, Positive(match.Context), Positive(match.Output));
        if (config.ModelLimits.TryGetValue(config.DefaultModel, out var discovered))
            return new(config.DefaultModel, known?.DisplayName ?? config.DefaultModel, Positive(discovered.ContextTokens) ?? known?.ContextTokens, Positive(discovered.OutputTokens) ?? known?.OutputTokens);
        return known;
    }
    public static ModelInfo Enrich(ApiConfig config, ModelInfo model)
    {
        var known = List(config).FirstOrDefault(m => m.Id == model.Id);
        return model with { ContextTokens = Positive(model.ContextTokens) ?? known?.ContextTokens, OutputTokens = Positive(model.OutputTokens) ?? known?.OutputTokens };
    }
    public static void Remember(ApiConfig config, IEnumerable<ModelInfo> models)
    {
        foreach (var model in models)
        {
            config.ModelLimits[model.Id] = new(model.ContextTokens, model.OutputTokens);
            if (model.ReasoningLevels != null) config.ModelReasoningLevels[model.Id] = model.ReasoningLevels.ToList();
        }
    }
    // Bound automatic generation while respecting model output and context limits.
    // Zero opts into automatic sizing; positive values remain an explicit user override.
    public static int OutputLimit(ApiConfig config)
    {
        var limit = Resolve(config)?.OutputTokens;
        var requested = config.MaxTokens > 0 ? config.MaxTokens : Math.Min(32000, ModelContext.Capacity(config) / 4);
        return Math.Min(Math.Min(requested, limit ?? 16384), Math.Max(1, ModelContext.Capacity(config) - 1024));
    }
}
