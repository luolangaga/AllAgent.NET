using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

/// <summary>Records one cumulative usage snapshot per request, including interrupted requests.</summary>
public sealed class UsageTrackingAiProvider(IAiProvider provider, DataStore store) : IAiProvider
{
    public string ProviderId => provider.ProviderId;
    public void EndRoutingTurn(Guid turnId) => provider.EndRoutingTurn(turnId);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
        => provider.ListModelsAsync(config, ct);

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var providerId = request.Config.ProviderId;
        var modelId = request.Config.DefaultModel;
        ModelTokenUsage? usage = null;
        try
        {
            await foreach (var chunk in provider.StreamChatAsync(request, ct))
            {
                // Streaming protocols may repeat cumulative usage or report input before output.
                if (chunk.Type == ChunkType.Usage && chunk.Usage is { } snapshot)
                    usage = new(Math.Max(usage?.InputTokens ?? 0, snapshot.InputTokens),
                        Math.Max(usage?.OutputTokens ?? 0, snapshot.OutputTokens))
                    {
                        CachedInputTokens = snapshot.CachedInputTokens is { } cached
                            ? Math.Max(usage?.CachedInputTokens ?? 0, cached) : usage?.CachedInputTokens,
                        CacheWriteInputTokens = snapshot.CacheWriteInputTokens is { } written
                            ? Math.Max(usage?.CacheWriteInputTokens ?? 0, written) : usage?.CacheWriteInputTokens
                    };
                yield return chunk;
            }
        }
        finally { store.RecordTokenUsage(providerId, modelId, usage, request.ConversationId); }
    }
}
