using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Services;

/// <summary>Host-enforced role limits. Null values inherit the host configuration.</summary>
public sealed record SubAgentExecutionSettings
{
    public double? Temperature { get; init; }
    public string? ReasoningEffort { get; init; }
    public int? ThinkingBudget { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int? MaxContextCharacters { get; init; }
    public int? MaxToolCalls { get; init; }
    public int? TimeoutSeconds { get; init; }
    public long? MaxTotalTokens { get; init; }
    public decimal? MaxCost { get; init; }
    public decimal? InputCostPerMillion { get; init; }
    public decimal? OutputCostPerMillion { get; init; }
    public int MaxConcurrent { get; init; } = 6;
    public int MaxDepth { get; init; } = 1;
    public bool IncludeProjectInstructions { get; init; } = true;
    public IReadOnlyList<string>? Skills { get; init; }
    public string FailurePolicy { get; init; } = "return";
    public int MaxRetries { get; init; } = 0;
    public string? FallbackProvider { get; init; }
    public string? FallbackModel { get; init; }

    public void Validate()
    {
        if (Temperature is { } t && (!double.IsFinite(t) || t < 0 || t > 2) ||
            MaxOutputTokens is <= 0 || ThinkingBudget is <= 0 || MaxContextCharacters is < 8000 ||
            MaxToolCalls is <= 0 || TimeoutSeconds is <= 0 or > 86400 || MaxTotalTokens is <= 0 ||
            MaxCost is <= 0 || InputCostPerMillion is < 0 || OutputCostPerMillion is < 0 ||
            MaxConcurrent is < 1 or > 6 || MaxDepth is < 0 or > 1 || MaxRetries is < 0 or > 20 ||
            FailurePolicy is not ("return" or "retry" or "fallback") ||
            MaxCost != null && (InputCostPerMillion == null || OutputCostPerMillion == null || InputCostPerMillion + OutputCostPerMillion == 0) ||
            FailurePolicy == "fallback" && string.IsNullOrWhiteSpace(FallbackProvider) && string.IsNullOrWhiteSpace(FallbackModel) ||
            FallbackModel?.Length > 256 || FallbackProvider?.Length > 256 || ReasoningEffort?.Length > 64 ||
            ThinkingBudget != null && ReasoningEffort != "budget" ||
            Skills is { } skills && (skills.Count > 256 || skills.Any(string.IsNullOrWhiteSpace) || skills.Any(s => s.Length > 256) || skills.Distinct(StringComparer.Ordinal).Count() != skills.Count))
            throw new ArgumentException("Invalid subagent execution settings (concurrency supports 1–6; depth supports 0 or 1; cost limits require explicit rates).");
    }

    public ApiConfig Apply(ApiConfig config)
    {
        Validate();
        var result = SubAgentModelSelection.Resolve(config, [config]);
        if (Temperature is { } temperature) result.Temperature = temperature;
        if (MaxOutputTokens is { } tokens) result.MaxTokens = result.MaxTokens > 0 ? Math.Min(result.MaxTokens, tokens) : tokens;
        if (ReasoningEffort != null) ReasoningSettings.Configure(result, ReasoningEffort, ThinkingBudget);
        return result;
    }

    public AgentOptions Apply(AgentOptions options) => options with
    {
        MaxContextCharacters = Math.Min(options.MaxContextCharacters, MaxContextCharacters ?? int.MaxValue),
        MaxToolCalls = Math.Min(options.MaxToolCalls, MaxToolCalls ?? int.MaxValue),
        RunTimeout = TimeoutSeconds is { } seconds ? options.RunTimeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(Math.Min(seconds, options.RunTimeout.TotalSeconds)) : options.RunTimeout,
        MaxModelRetries = FailurePolicy == "return" ? 0 : Math.Min(options.MaxModelRetries, MaxRetries)
    };
}

/// <summary>Six shared slots per store plus role limits, including concurrent parent conversations.</summary>
public static class SubAgentConcurrency
{
    private sealed class Counter
    {
        public int Active;
        public readonly Dictionary<string, int> Roles = new(StringComparer.Ordinal);
    }
    private static readonly ConditionalWeakTable<DataStore, Counter> Stores = new();
    public static async Task<IDisposable> AcquireAsync(DataStore store, string role, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(limit));
        var counter = Stores.GetOrCreateValue(store);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (counter)
            {
                var roleActive = counter.Roles.GetValueOrDefault(role);
                if (counter.Active < 6 && roleActive < limit)
                {
                    counter.Active++;
                    counter.Roles[role] = roleActive + 1;
                    return new Lease(counter, role);
                }
            }
            await Task.Delay(50, ct);
        }
    }
    private sealed class Lease(Counter counter, string role) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (counter) { counter.Active--; counter.Roles[role]--; } }
    }
}
