using System.Text.Json.Nodes;

namespace LanAgent.Core.Services.AI;

internal static class PromptCacheRouting
{
    internal static void Apply(JsonObject body, ChatRequest request)
    {
        // Do not assume OpenAI-compatible proxies accept OpenAI-specific fields.
        // Keep affinity across user turns, independently of per-turn routing state.
        if (request.ConversationId is { } session &&
            Uri.TryCreate(request.Config.BaseUrl, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            body["prompt_cache_key"] = "lanagent:" + session.ToString("N");
    }
}
