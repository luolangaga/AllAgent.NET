namespace AllAgent.NET.Core.Models;

/// <summary>One model's usage for one local calendar day; feeds the browser's activity graph.</summary>
public sealed record DailyUsageStats(string Date, string ProviderId, string ModelId, long InputTokens = 0,
    long OutputTokens = 0, long Requests = 0, long RequestsWithUsage = 0);
