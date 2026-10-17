using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

internal static class PromptCacheRouting
{
    internal static void Apply(JsonObject body, ChatRequest request)
    {
        // Do not assume OpenAI-compatible proxies accept OpenAI-specific fields.
        // Keep affinity across user turns, independently of per-turn routing state.
        if (request.ConversationId is { } session && OfficialEndpoint(request.Config))
            body["prompt_cache_key"] = "allagent:" + session.ToString("N");
    }

    /// <summary>Opt-in extended retention; GPT-5.6+ replaced the field with prompt_cache_options.ttl.</summary>
    internal static void ApplyRetention(JsonObject body, ChatRequest request)
    {
        if (request.PromptCacheRetention != "24h") return;
        if (!OfficialEndpoint(request.Config)) return;
        var model = request.Config.DefaultModel.ToLowerInvariant();
        if (model.StartsWith("gpt-5.6") || model.StartsWith("gpt-6")) return;
        if (!model.StartsWith("gpt-4.1") && !model.StartsWith("gpt-5")) return;
        body["prompt_cache_retention"] = "24h";
    }

    private static bool OfficialEndpoint(ApiConfig config) =>
        Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
}
