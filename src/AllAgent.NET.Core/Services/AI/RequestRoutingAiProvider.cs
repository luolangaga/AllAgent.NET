using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

/// <summary>Selects the host's configured/authenticated provider for each request snapshot.</summary>
public sealed class RequestRoutingAiProvider(Func<ApiConfig, IAiProvider> resolve) : IAiProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentDictionary<IAiProvider, byte>> _turnProviders = new();
    public void EndRoutingTurn(Guid turnId)
    {
        if (_turnProviders.TryRemove(turnId, out var providers)) foreach (var provider in providers.Keys) provider.EndRoutingTurn(turnId);
    }
    public string ProviderId => "host-routing";
    public IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var provider = resolve(request.Config);
        if (request.RoutingTurnId is { } turn) _turnProviders.GetOrAdd(turn, _ => new()).TryAdd(provider, 0);
        return provider.StreamChatAsync(request, ct);
    }
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
        => resolve(config).ListModelsAsync(config, ct);
}
