using System;

namespace AllAgent.NET.Core.Models;

public class ApiConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = ""; // "openai" | "anthropic" | "gemini" | "ollama"
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 0;
    public int ContextWindowTokens { get; set; } = 100000;
    public Dictionary<string, int> ModelContextWindows { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ModelInfoLimits> ModelLimits { get; set; } = new(StringComparer.Ordinal);
    public bool IsEnabled { get; set; } = true;
    public string? ExtraHeadersJson { get; set; }
    public bool ThinkingEnabled { get; set; } = false;
    public int ThinkingBudget { get; set; } = 8000;
    public string Protocol { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public string ReasoningProtocol { get; set; } = "auto";
    public Dictionary<string, string> ModelReasoningEfforts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ModelThinkingBudgets { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ModelReasoningLevels { get; set; } = new(StringComparer.Ordinal);
    public string ReasoningEffort { get; set; } = "default";
    public string? ApiKeyEnvironmentVariable { get; set; }
    public string? AuthenticationProviderId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticationContextId { get; set; }
}

public sealed record ModelInfoLimits(int? ContextTokens, int? OutputTokens);
