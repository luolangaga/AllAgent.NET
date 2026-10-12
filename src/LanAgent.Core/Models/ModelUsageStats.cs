namespace LanAgent.Core.Models;

/// <summary>Lifetime usage reported by a provider, independent of conversation history.</summary>
public sealed record ModelUsageStats(string ProviderId, string ModelId, long InputTokens = 0,
    long OutputTokens = 0, long Requests = 0, long RequestsWithUsage = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public long RequestsWithoutUsage => Requests - RequestsWithUsage;
}
