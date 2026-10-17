using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

public sealed record ReasoningCapabilities(string Protocol, IReadOnlyList<string> Efforts, int MinimumBudget = 0, int MaximumBudget = 0);

/// <summary>Model variants and native wire controls. Sources: docs/reasoning-providers.md.</summary>
internal static class ReasoningPolicy
{
    internal static string Model(ApiConfig c) => c.DefaultModel.ToLowerInvariant().Split('/').Last();
    private static bool ClaudeVersion(string model, int minor) => Regex.IsMatch(model, @"claude-(?:[a-z]+-)?(?:[5-9](?:[.\-]|$)|4[.\-][" + minor + @"-9](?:[.\-]|$))");
    internal static string Protocol(ApiConfig c)
    {
        var model = Model(c); var transport = ProviderCatalog.ProtocolFor(c);
        if (c.ReasoningProtocol != "auto") return c.ReasoningProtocol switch
        {
            "anthropic-adaptive" when model.StartsWith("claude-") && !ClaudeVersion(model, 6) => "anthropic-budget",
            "anthropic-budget" when ClaudeVersion(model, 6) => "anthropic-adaptive",
            "gemini-level" when model.StartsWith("gemini-2.5") => "gemini-budget",
            "gemini-budget" when model.StartsWith("gemini-3") || model.StartsWith("gemini-4") => "gemini-level",
            _ => c.ReasoningProtocol
        };
        var provider = c.ProviderId;
        if (Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var uri)) provider = uri.Host switch
        {
            "api.deepseek.com" => "deepseek", "openrouter.ai" => "openrouter", "api.openai.com" => "openai",
            "dashscope.aliyuncs.com" or "dashscope-intl.aliyuncs.com" => "dashscope", _ => provider
        };
        if (provider == "openrouter") return "openrouter";
        if (provider == "deepseek") return "deepseek";
        if (transport == "anthropic") return model.StartsWith("claude-") && !ClaudeVersion(model, 6) ? "anthropic-budget" : "anthropic-adaptive";
        if (transport == "gemini") return model.StartsWith("gemini-2.5") ? "gemini-budget" : "gemini-level";
        return provider switch
        {
            "dashscope" => "qwen", "xai" => "xai", "moonshot" or "zhipu" => "toggle",
            _ => ProviderCatalog.Find(provider)?.ReasoningProtocol ?? "none"
        };
    }
    internal static ReasoningCapabilities Describe(ApiConfig c)
    {
        var protocol = Protocol(c); var model = Model(c);
        string[] levels = protocol switch
        {
            "openai" => OpenAi(model),
            "deepseek" when model is "deepseek-reasoner" or "deepseek-chat" || model.Contains("deepseek-r1") => [],
            "deepseek" when model.Contains("deepseek-v3") => ["none", "thinking"],
            "deepseek" => ["none", "low", "high", "max"],
            "openrouter" when model.StartsWith("gpt-") || Regex.IsMatch(model, @"^o[134](?:-|$)") => OpenAi(model),
            "openrouter" when model.Contains("grok-3-mini") => ["low", "high"],
            "openrouter" when model.Contains("qwen") || model.Contains("kimi") || model.Contains("minimax") || model.Contains("deepseek-r1") || model.Contains("deepseek-reasoner") => [],
            "openrouter" => ["none", "low", "medium", "high"],
            "anthropic-adaptive" when ClaudeVersion(model, 7) => ["low", "medium", "high", "xhigh", "max"],
            "anthropic-adaptive" => ["low", "medium", "high", "max"],
            "anthropic-budget" => ["none", "high", "max", "budget"],
            "gemini-budget" when model.Contains("pro") => ["high", "max", "budget"],
            "gemini-budget" => ["none", "high", "max", "budget"],
            "gemini-level" when model.Contains("flash-image") => ["minimal", "high"],
            "gemini-level" when model.Contains("pro-image") => ["high"],
            "gemini-level" when model.Contains("flash") => ["minimal", "low", "medium", "high"],
            "gemini-level" when model.StartsWith("gemini-3-pro") => ["low", "high"],
            "gemini-level" => ["low", "medium", "high"],
            "qwen" when model.StartsWith("glm-5.3") => ["high", "max"],
            "qwen" when model.Contains("deepseek-v4") || model.StartsWith("glm-5") => ["none", "high", "max"],
            "qwen" when model.StartsWith("qwen3.8-2.4t") => ["low", "medium", "xhigh", "budget"],
            "qwen" when model.StartsWith("qwen3.8") => ["none", "low", "medium", "xhigh", "budget"],
            "qwen" when FixedQwen(model) => ["thinking", "budget"],
            "qwen" when model.Contains("instruct") || model.Contains("coder") && !model.Contains("thinking") => [],
            "qwen" when model.Contains("thinking") || model.StartsWith("qwq") => ["thinking", "budget"],
            "qwen" => ["none", "thinking", "budget"],
            "toggle" when model.StartsWith("glm-5.2") => ["none", "high", "max"],
            "toggle" when model.Contains("thinking") || model.StartsWith("glm-5.3") => [],
            "toggle" => ["none", "thinking"],
            "xai" when model.StartsWith("grok-3-mini") => ["low", "high"],
            _ => []
        };
        if (ModelCatalog.SupportsReasoning(c) == false) levels = [];
        // The authenticated endpoint's advertised subset takes priority over family fallbacks.
        if (protocol is "openai" or "openrouter" && c.ModelReasoningLevels.TryGetValue(c.DefaultModel, out var advertised))
            levels = advertised.Where(level => level is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra").Distinct().ToArray();
        var min = protocol switch { "anthropic-budget" => 1024, "gemini-budget" when model.Contains("pro") => 128, "gemini-budget" when model.Contains("lite") => 512, _ => 1 };
        var max = protocol switch
        {
            "anthropic-budget" => Math.Min(131072, ModelCatalog.OutputLimit(c) - 1),
            "gemini-budget" => model.Contains("pro") ? 32768 : 24576,
            "qwen" => 131072, _ => 0
        };
        if (protocol == "anthropic-budget" && max < min) levels = levels.Where(level => level == "none").ToArray();
        return new(protocol, ["default", .. levels], min, max);
    }
    private static string[] OpenAi(string id)
    {
        if (id.StartsWith("gpt-4") || id.StartsWith("gpt-3") || id is "o1-mini" or "o1-preview" or "gpt-5-chat-latest") return [];
        if (id.Contains("deep-research")) return ["medium"];
        if (id.StartsWith("gpt-6-astra") || id.StartsWith("gpt-6.1-sol")) return ["low", "medium", "high", "xhigh", "max"];
        if (id.StartsWith("gpt-6") || id.StartsWith("gpt-5.6")) return ["none", "low", "medium", "high", "xhigh", "max"];
        if (id == "gpt-5-pro") return ["high"];
        if (Regex.IsMatch(id, @"^gpt-5[.\-]\d+[.\-]pro(?:-|$)")) return ["medium", "high", "xhigh"];
        if (Regex.IsMatch(id, @"^gpt-5[.\-]\d+.*chat")) return ["medium"];
        if (id.Contains("codex"))
        {
            if (Regex.IsMatch(id, @"^gpt-5[.\-][3-9]")) return ["none", "low", "medium", "high", "xhigh"];
            if (id.Contains("codex-max") || Regex.IsMatch(id, @"^gpt-5[.\-][2-9]")) return ["low", "medium", "high", "xhigh"];
            return ["low", "medium", "high"];
        }
        if (id.StartsWith("gpt-5.1")) return ["none", "low", "medium", "high"];
        if (Regex.IsMatch(id, @"^gpt-5[.\-][2-9]")) return ["none", "low", "medium", "high", "xhigh"];
        return id.StartsWith("gpt-5") ? ["minimal", "low", "medium", "high"] : ["low", "medium", "high"];
    }
    internal static void ValidateBudget(int budget, ReasoningCapabilities profile)
    {
        if (budget < profile.MinimumBudget || budget > profile.MaximumBudget)
            throw new ArgumentException($"{profile.Protocol} 思考预算范围为 {profile.MinimumBudget}–{profile.MaximumBudget} tokens（已考虑模型输出上限）。");
    }
    internal static int? Budget(ApiConfig c, string effort)
    {
        var p = Describe(c);
        if (effort == "budget") { ValidateBudget(c.ThinkingBudget, p); return c.ThinkingBudget; }
        if (p.Protocol is not ("anthropic-budget" or "gemini-budget") || effort is not ("high" or "max")) return null;
        return effort == "high" ? Math.Min(16000, p.MaximumBudget) : p.Protocol == "gemini-budget" ? p.MaximumBudget : Math.Min(31999, p.MaximumBudget);
    }
    internal static void Apply(JsonObject body, ApiConfig c)
    {
        var p = Describe(c); var effort = c.ReasoningEffort; var transport = ProviderCatalog.ProtocolFor(c);
        if (!p.Efforts.Contains(effort)) throw new ArgumentException($"{c.DefaultModel} 不支持 {effort}；请重新设置思考强度。");
        // Compatibility with the old boolean UI, constrained by the model's native controls.
        if (effort == "default" && c.ThinkingEnabled && p.Efforts.Count > 1)
            effort = p.Efforts.Contains("budget") ? "budget" : p.Efforts.Contains("thinking") ? "thinking" : p.Efforts.Contains("high") ? "high" : p.Efforts.Last();
        if (p.Protocol == "openai" && p.Efforts.Count > 1)
        {
            if (effort != "none") body.Remove("temperature");
            if (transport != "responses") { body["max_completion_tokens"] = ModelCatalog.OutputLimit(c); body.Remove("max_tokens"); }
        }
        if (effort == "default")
        {
            if (p.Efforts.Count > 1 && p.Protocol.StartsWith("gemini-")) Google(body)["includeThoughts"] = true;
            if (p.Efforts.Count > 1 && p.Protocol == "openai" && transport == "responses") body["reasoning"] = new JsonObject { ["summary"] = "auto" };
            return;
        }
        var budget = Budget(c, effort);
        switch (p.Protocol)
        {
            case "openai":
                if (transport == "responses") body["reasoning"] = new JsonObject { ["effort"] = effort, ["summary"] = "auto" };
                else { body["reasoning_effort"] = effort; body["max_completion_tokens"] = ModelCatalog.OutputLimit(c); body.Remove("max_tokens"); }
                body.Remove("temperature"); break;
            case "deepseek":
                if (transport == "responses") body["reasoning"] = new JsonObject { ["effort"] = effort == "thinking" ? "high" : effort };
                else
                {
                    body["thinking"] = new JsonObject { ["type"] = effort == "none" ? "disabled" : "enabled" };
                    if (effort is not ("none" or "thinking"))
                    {
                        if (transport == "anthropic") body["output_config"] = new JsonObject { ["effort"] = effort };
                        else body["reasoning_effort"] = effort;
                    }
                }
                body.Remove("temperature"); break;
            case "openrouter":
                body["reasoning"] = effort == "none" ? new JsonObject { ["enabled"] = false } : new JsonObject { ["effort"] = effort };
                body["provider"] ??= new JsonObject(); body["provider"]!["require_parameters"] = true;
                body.Remove("temperature"); break;
            case "qwen":
                if (!FixedQwen(Model(c))) body["enable_thinking"] = effort != "none";
                if (budget != null) body["thinking_budget"] = budget.Value;
                else if (effort is not ("none" or "thinking")) body["reasoning_effort"] = effort;
                break;
            case "toggle":
                body["thinking"] = new JsonObject { ["type"] = effort == "none" ? "disabled" : "enabled" };
                if (effort is not ("none" or "thinking")) body["reasoning_effort"] = effort;
                break;
            case "xai": body["reasoning_effort"] = effort; break;
            case "anthropic-adaptive":
                body["thinking"] = new JsonObject { ["type"] = "adaptive" };
                if (ClaudeVersion(Model(c), 7)) body["thinking"]!["display"] = "summarized";
                body["output_config"] = new JsonObject { ["effort"] = effort }; body.Remove("temperature"); break;
            case "anthropic-budget":
                body["thinking"] = effort == "none" ? new JsonObject { ["type"] = "disabled" } : new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget!.Value };
                body.Remove("temperature"); break;
            case "gemini-level": Google(body)["thinkingLevel"] = effort; Google(body)["includeThoughts"] = true; break;
            case "gemini-budget": Google(body)["thinkingBudget"] = effort == "none" ? 0 : budget!.Value; Google(body)["includeThoughts"] = true; break;
        }
    }
    private static JsonObject Google(JsonObject body)
    {
        body["generationConfig"] ??= new JsonObject(); body["generationConfig"]!["thinkingConfig"] ??= new JsonObject();
        return body["generationConfig"]!["thinkingConfig"]!.AsObject();
    }
    private static bool FixedQwen(string model) => model.Contains("thinking") || model.StartsWith("qwq")
        || model.StartsWith("qwen3.8-2.4t") || model is "qwen3.7-max-preview" or "qwen3.7-max-2026-05-17";
}
