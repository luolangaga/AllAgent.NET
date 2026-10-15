using System.Net.Http;
using System.Text.Json.Nodes;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public static class ModelDiscovery
{
    public static async Task<IReadOnlyList<ModelInfo>> ListAsync(ApiConfig original, CancellationToken ct = default)
    {
        await ModelCatalog.RefreshAsync(ct);
        var config = ProviderCatalog.ResolveCredentials(original);
        using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(20));
        var protocol = ProviderCatalog.ProtocolFor(config);
        var root = config.BaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Base URL 必须是 HTTP(S) 地址。");
        var url = protocol switch { "anthropic" => root + (root.EndsWith("/v1") ? "/models" : "/v1/models"), "gemini" => root + (root.EndsWith("/v1beta") ? "/models" : "/v1beta/models"), _ => root + "/models" };
        if (protocol == "anthropic") { http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", config.ApiKey); http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01"); }
        else if (protocol == "gemini") http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", config.ApiKey);
        else if (config.ApiKey.Length > 0) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
        if (!string.IsNullOrEmpty(config.ExtraHeadersJson))
            foreach (var pair in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(config.ExtraHeadersJson)!) http.DefaultRequestHeaders.TryAddWithoutValidation(pair.Key, pair.Value);
        var models = new List<ModelInfo>();
        for (var page = 0; page < 20; page++)
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"获取模型列表失败：HTTP {(int)response.StatusCode}。可手动填写模型 ID；原列表已保留。");
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = (protocol == "gemini" ? node?["models"] : node?["data"]) as JsonArray ?? throw new InvalidOperationException("接口未返回有效模型列表。");
            foreach (var item in items)
            {
                var id = protocol == "gemini" ? item?["name"]?.GetValue<string>()?.Replace("models/", "") : item?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id)) models.Add(new(id, item?["display_name"]?.GetValue<string>() ?? item?["displayName"]?.GetValue<string>() ?? id,
                    (int?)(item?["inputTokenLimit"] ?? item?["context_length"] ?? item?["context_window"]),
                    (int?)(item?["outputTokenLimit"] ?? item?["top_provider"]?["max_completion_tokens"] ?? item?["max_output_tokens"])));
            }
            var token = node?["nextPageToken"]?.GetValue<string>();
            var last = node?["last_id"]?.GetValue<string>();
            if (protocol == "gemini" && !string.IsNullOrEmpty(token)) url = root + (root.EndsWith("/v1beta") ? "/models" : "/v1beta/models") + "?pageToken=" + Uri.EscapeDataString(token);
            else if (protocol == "anthropic" && node?["has_more"]?.GetValue<bool>() == true && !string.IsNullOrEmpty(last)) url = root + (root.EndsWith("/v1") ? "/models" : "/v1/models") + "?after_id=" + Uri.EscapeDataString(last);
            else break;
        }
        return models.DistinctBy(m => m.Id).OrderBy(m => m.Id).ToArray();
    }
}
