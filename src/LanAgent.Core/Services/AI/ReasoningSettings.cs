using System.Text.Json.Nodes;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public static class ReasoningSettings
{
    public static IReadOnlyList<string> Protocols { get; } = new[] { "none", "openai", "deepseek", "qwen", "openrouter", "anthropic-adaptive", "anthropic-budget", "gemini-level", "gemini-budget" };
    public static IReadOnlyList<string> Efforts(ApiConfig config) => ModelCatalog.SupportsReasoning(config) == false ? new[] { "default" } : ProviderCatalog.ReasoningFor(config) switch
    {
        "openai" => new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh" },
        "deepseek" => new[] { "default", "none", "low", "high", "max" },
        "openrouter" => new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh" },
        "anthropic-adaptive" => new[] { "default", "low", "medium", "high", "max" },
        "anthropic-budget" or "qwen" or "gemini-budget" => new[] { "default", "none", "low", "medium", "high", "budget" },
        "gemini-level" => new[] { "default", "minimal", "low", "medium", "high" },
        _ => new[] { "default" }
    };
    public static void SetEffort(ApiConfig config, string effort)
    {
        if (!Efforts(config).Contains(effort)) throw new ArgumentException($"{ProviderCatalog.ReasoningFor(config)} 不支持 {effort}；可选：{string.Join(", ", Efforts(config))}。具体模型还可能限制这些级别。");
        config.ReasoningEffort = effort;
        config.ModelReasoningEfforts[config.DefaultModel] = effort;
        config.ThinkingEnabled = effort is not ("default" or "none");
    }
    public static void RestoreForModel(ApiConfig config)
    {
        var effort = config.ModelReasoningEfforts.GetValueOrDefault(config.DefaultModel, "default");
        SetEffort(config, Efforts(config).Contains(effort) ? effort : "default");
    }
    public static void Apply(JsonObject body, ApiConfig config)
    {
        var protocol = ProviderCatalog.ReasoningFor(config);
        var effort = config.ReasoningEffort;
        // Legacy ThinkingEnabled means a budget, but never emit proprietary fields to an unrelated API.
        if (effort == "default" && !config.ThinkingEnabled) return;
        if (!Efforts(config).Contains(effort)) throw new ArgumentException("思考强度与配置的协议不匹配。");
        var budget = effort switch { "low" => 2048, "medium" => 8192, "high" => 16384, _ => config.ThinkingBudget };
        if (budget < 1024 || budget > 131072) throw new ArgumentException("思考预算必须在 1024–131072 之间。");
        switch (protocol)
        {
            case "none": throw new ArgumentException("此配置未启用思考参数协议，请在模型管理中选择接口实际支持的协议。");
            case "openai":
                if (effort != "default") body["reasoning_effort"] = effort;
                body.Remove("temperature");
                body["max_completion_tokens"] = ModelCatalog.OutputLimit(config); body.Remove("max_tokens"); break;
            case "deepseek":
                body["thinking"] = new JsonObject { ["type"] = effort == "none" ? "disabled" : "enabled" };
                if (effort is not ("default" or "none")) body["reasoning_effort"] = effort;
                body.Remove("temperature"); break;
            case "openrouter":
                body["reasoning"] = effort == "none" ? new JsonObject { ["enabled"] = false } : effort == "default" ? new JsonObject { ["enabled"] = true } : new JsonObject { ["effort"] = effort };
                body.Remove("temperature"); break;
            case "qwen":
                body["enable_thinking"] = effort != "none";
                if (effort != "none") body["thinking_budget"] = budget; break;
            case "anthropic-adaptive":
                body["thinking"] = new JsonObject { ["type"] = "adaptive" };
                if (effort != "default") body["output_config"] = new JsonObject { ["effort"] = effort };
                body.Remove("temperature"); break;
            case "anthropic-budget":
                body["thinking"] = effort == "none" ? new JsonObject { ["type"] = "disabled" } : new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
                if (effort != "none" && ModelCatalog.OutputLimit(config) <= budget) throw new ArgumentException("Anthropic 输出上限必须大于思考预算。");
                body.Remove("temperature"); break;
            case "gemini-level":
            case "gemini-budget":
                var generation = body["generationConfig"]!.AsObject();
                generation["thinkingConfig"] = protocol == "gemini-level"
                    ? new JsonObject { ["thinkingLevel"] = effort == "default" ? "high" : effort, ["includeThoughts"] = true }
                    : new JsonObject { ["thinkingBudget"] = effort == "none" ? 0 : budget, ["includeThoughts"] = true }; break;
        }
    }
}
