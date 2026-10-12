using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

/// <summary>Selects the host's configured/authenticated provider for each request snapshot.</summary>
public sealed class RequestRoutingAiProvider(Func<ApiConfig, IAiProvider> resolve) : IAiProvider
{
    public string ProviderId => "host-routing";
    public IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
        => resolve(request.Config).StreamChatAsync(request, ct);
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
        => resolve(config).ListModelsAsync(config, ct);
}
