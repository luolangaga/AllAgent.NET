using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public enum ChunkType { TextDelta, ThinkingDelta, ToolCallDelta, ToolCallComplete, Done, Error, ProviderState, Usage }
public sealed record ModelTokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Cached prompt tokens, null when the provider does not expose cache usage.</summary>
    public int? CachedInputTokens { get; init; }
}

public record ToolCallChunk(string Id, string Name, string ArgumentsDelta);

public record StreamChunk(
    ChunkType Type,
    string? TextDelta = null,
    ToolCallChunk? ToolCall = null,
    string? FinishReason = null,
    string? ErrorMessage = null,
    string? ProviderStateJson = null
)
{
    public ModelTokenUsage? Usage { get; init; }
}

public record ChatMessage(string Role, string Content, string? ToolCallsJson = null, string? ToolCallId = null, string? ToolName = null, string? ReasoningContent = null, string? ProviderStateJson = null)
{
    public string? ProviderContextKey { get; init; }
    /// <summary>Learned memory/profile tail, excluded from stable prompt cache breakpoints.</summary>
    public bool IsVolatileContext { get; init; }
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
}

public record SkillDefinition(string Name, string Description, object Parameters);

public record ChatRequest(
    ApiConfig Config,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<SkillDefinition>? Tools = null
)
{
    public Guid? ConversationId { get; init; }
    public Guid? RoutingTurnId { get; init; }
}

public record ModelInfo(string Id, string DisplayName, int? ContextTokens = null, int? OutputTokens = null);

public interface IAiProvider
{
    void EndRoutingTurn(Guid turnId) { }
    string ProviderId { get; }
    IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default);
}
