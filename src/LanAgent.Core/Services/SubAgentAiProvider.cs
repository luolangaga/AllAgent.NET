using System.Runtime.CompilerServices;
using System.Text;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.Core.Services;

/// <summary>Per-run budget accounting also covers compaction requests and model retries.</summary>
public sealed class SubAgentAiProvider(IAiProvider inner, SubAgentExecutionSettings settings,
    Func<ApiConfig, ApiConfig>? fallback = null) : IAiProvider
{
    private long _tokens;
    private decimal _cost;
    private ApiConfig? _fallback;
    public string ProviderId => inner.ProviderId;
    public void EndRoutingTurn(Guid id) => inner.EndRoutingTurn(id);
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => inner.ListModelsAsync(config, ct);

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        settings.Validate();
        if ((settings.MaxTotalTokens != null || settings.MaxCost != null) && request.Messages.Any(m => m.Images.Count > 0))
            throw new SubAgentBudgetExceededException("Image token pricing cannot be estimated locally; remove images or use a role without a token/cost limit.");
        var config = _fallback ?? settings.Apply(request.Config);
        // UTF-8 bytes are a conservative local estimate when usage is unavailable.
        long input = request.Messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Content));
        long output = 0;
        long reportedInput = 0, reportedOutput = 0;
        Check(input, output);
        var emitted = false;
        var switched = _fallback != null;
        while (true)
        {
            var messages = switched ? request.Messages.Select(m => m with { ProviderStateJson = null, ProviderContextKey = null }).ToArray() : request.Messages;
            await using var stream = inner.StreamChatAsync(request with { Config = config, Messages = messages }, ct).GetAsyncEnumerator(ct);
            var retry = false;
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await stream.MoveNextAsync(); }
                    catch (Exception ex) when (!emitted && !switched && fallback != null && settings.FailurePolicy == "fallback" && ex is HttpRequestException or IOException or TimeoutException)
                    { retry = true; break; }
                    if (!next) break;
                    var chunk = stream.Current;
                    if (chunk.Type == ChunkType.Error && !emitted && !switched && fallback != null && settings.FailurePolicy == "fallback")
                    { retry = true; break; }
                    if (chunk.Type is ChunkType.TextDelta or ChunkType.ThinkingDelta) output += Encoding.UTF8.GetByteCount(chunk.TextDelta ?? "");
                    if (chunk.ToolCall != null) output += Encoding.UTF8.GetByteCount(chunk.ToolCall.ArgumentsDelta ?? "");
                    if (chunk.Usage is { } usage)
                    {
                        reportedInput = Math.Max(reportedInput, usage.InputTokens);
                        reportedOutput = Math.Max(reportedOutput, usage.OutputTokens);
                    }
                    Check(Math.Max(input, reportedInput), Math.Max(output, reportedOutput));
                    emitted = true;
                    yield return chunk;
                }
            }
            finally
            {
                var billedInput = Math.Max(input, reportedInput);
                var billedOutput = Math.Max(output, reportedOutput);
                _tokens += billedInput + billedOutput;
                _cost += Cost(billedInput, billedOutput);
            }
            if (!retry) yield break;
            config = _fallback = fallback!(request.Config);
            if (settings.Temperature is { } temperature) config.Temperature = temperature;
            if (settings.MaxOutputTokens is { } maxOutput) config.MaxTokens = config.MaxTokens > 0 ? Math.Min(config.MaxTokens, maxOutput) : maxOutput;
            switched = true;
            Check(input, output);
        }
    }

    private decimal Cost(long input, long output) => (input * (settings.InputCostPerMillion ?? 0) + output * (settings.OutputCostPerMillion ?? 0)) / 1_000_000m;
    private void Check(long input, long output)
    {
        if (settings.MaxTotalTokens is { } maxTokens && _tokens + input + output > maxTokens ||
            settings.MaxCost is { } maxCost && _cost + Cost(input, output) > maxCost)
            throw new SubAgentBudgetExceededException("Subagent token/cost budget reached; return control to the parent. Usage includes conservative local estimates.");
    }
}

public sealed class SubAgentBudgetExceededException(string message) : Exception(message);
