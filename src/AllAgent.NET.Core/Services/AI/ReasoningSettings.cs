using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

public static class ReasoningSettings
{
    public static IReadOnlyList<string> Protocols { get; } = ["auto", "none", "openai", "deepseek", "workbuddy", "commandcode", "qoder", "qwen", "openrouter", "anthropic-adaptive", "anthropic-budget", "gemini-level", "gemini-budget", "toggle", "xai"];
    public static ReasoningCapabilities Capabilities(ApiConfig config) => ReasoningPolicy.Describe(config);
    public static IReadOnlyList<string> Efforts(ApiConfig config) => Capabilities(config).Efforts;
    public static int? Budget(ApiConfig config) => ReasoningPolicy.Budget(config, config.ReasoningEffort);
    public static void RememberForModel(ApiConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DefaultModel)) return;
        config.ModelReasoningEfforts[config.DefaultModel] = config.ReasoningEffort;
        config.ModelThinkingBudgets[config.DefaultModel] = config.ThinkingBudget;
    }
    public static void SetEffort(ApiConfig config, string effort) => Configure(config, effort);
    public static void Configure(ApiConfig config, string effort, int? budget = null)
    {
        var profile = Capabilities(config);
        if (!profile.Efforts.Contains(effort)) throw new ArgumentException($"{config.DefaultModel}（{profile.Protocol}）不支持 {effort}；可选：{string.Join(", ", profile.Efforts)}。");
        if (budget != null && effort != "budget") throw new ArgumentException("仅 budget 档位接受 Token 预算；其他档位使用接口原生强度。");
        if (effort == "budget") ReasoningPolicy.ValidateBudget(budget ?? config.ThinkingBudget, profile);
        config.ReasoningEffort = effort;
        if (budget != null) config.ThinkingBudget = budget.Value;
        config.ThinkingEnabled = effort is not ("default" or "none");
        RememberForModel(config);
    }
    public static void RestoreForModel(ApiConfig config)
    {
        var effort = config.ModelReasoningEfforts.GetValueOrDefault(config.DefaultModel, "default");
        var budget = config.ModelThinkingBudgets.GetValueOrDefault(config.DefaultModel, config.ThinkingBudget);
        var profile = Capabilities(config);
        if (!profile.Efforts.Contains(effort) || effort == "budget" && (budget < profile.MinimumBudget || budget > profile.MaximumBudget)) effort = "default";
        config.ThinkingBudget = budget;
        SetEffort(config, effort);
    }
    public static string Label(ApiConfig config) => config.ReasoningEffort switch
    {
        "default" => "default（模型默认）", "none" => "none（关闭）", "thinking" => "thinking（开启）",
        _ => config.ReasoningEffort + (Budget(config) is { } budget ? $" ({budget} tokens)" : "")
    };
    public static void Apply(JsonObject body, ApiConfig config) => ReasoningPolicy.Apply(body, config);
}
