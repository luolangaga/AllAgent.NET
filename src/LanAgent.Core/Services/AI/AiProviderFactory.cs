using System.Collections.Generic;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public class AiProviderFactory
{
    private readonly Dictionary<string, IAiProvider> _providers;

    public AiProviderFactory(IEnumerable<IAiProvider>? extensions = null)
    {
        _providers = new Dictionary<string, IAiProvider>
        {
            ["openai"] = new OpenAiProvider(),
            ["anthropic"] = new AnthropicProvider(),
            ["gemini"] = new GeminiProvider(),
            ["ollama"] = new OpenAiProvider(),
            ["responses"] = new ResponsesProvider(),
        };
        foreach (var provider in extensions ?? [])
            if (!_providers.TryAdd(provider.ProviderId, provider)) throw new ArgumentException("Provider ID collision: " + provider.ProviderId);
    }

    public IAiProvider GetProvider(string providerId)
        => _providers.TryGetValue(providerId, out var p) ? p : ProviderCatalog.Find(providerId) is { } preset
            ? _providers[preset.Protocol] : throw new ArgumentException($"Unknown AI provider: {providerId}. Configure an explicit protocol for custom providers.");

    public IAiProvider GetProvider(ApiConfig config) => GetProvider(string.IsNullOrWhiteSpace(config.Protocol) ? config.ProviderId : config.Protocol);
}
