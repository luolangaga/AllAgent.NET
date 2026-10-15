using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public sealed record ProviderPreset(string Id, string Name, string Protocol, string BaseUrl, string ReasoningProtocol);

/// <summary>Editable connection presets. Models are discovered from the configured endpoint, never fabricated.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderPreset> All { get; } = new ProviderPreset[]
    {
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1", "openai"),
        new("codex", "Codex / ChatGPT", "responses", "https://api.openai.com/v1", "openai"),
        new("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", "anthropic-adaptive"),
        new("gemini", "Google Gemini", "gemini", "https://generativelanguage.googleapis.com", "gemini-level"),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com", "deepseek"),
        new("dashscope", "阿里云百炼 / Qwen", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen"),
        new("moonshot", "Moonshot / Kimi", "openai", "https://api.moonshot.cn/v1", "none"),
        new("zhipu", "智谱 / GLM", "openai", "https://open.bigmodel.cn/api/paas/v4", "none"),
        new("siliconflow", "硅基流动", "openai", "https://api.siliconflow.cn/v1", "none"),
        new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1", "openrouter"),
        new("groq", "Groq", "openai", "https://api.groq.com/openai/v1", "openai"),
        new("mistral", "Mistral", "openai", "https://api.mistral.ai/v1", "none"),
        new("xai", "xAI / Grok", "openai", "https://api.x.ai/v1", "openai"),
        new("ollama", "Ollama", "openai", "http://127.0.0.1:11434/v1", "none"),
        new("lmstudio", "LM Studio", "openai", "http://127.0.0.1:1234/v1", "none"),
        new("custom", "自定义提供商", "openai", "", "none")
    };
    public static ProviderPreset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? ModelCatalog.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public static string ProtocolFor(ApiConfig config) => string.IsNullOrWhiteSpace(config.Protocol) ? Find(config.ProviderId)?.Protocol ?? config.ProviderId : config.Protocol;
    public static string ReasoningFor(ApiConfig config) => config.ReasoningProtocol == "auto" ? Find(config.ProviderId)?.ReasoningProtocol ?? "none" : config.ReasoningProtocol;
    public static ApiConfig ResolveCredentials(ApiConfig config)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<ApiConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        if (!string.IsNullOrWhiteSpace(copy.ApiKeyEnvironmentVariable))
            copy.ApiKey = Environment.GetEnvironmentVariable(copy.ApiKeyEnvironmentVariable) ?? throw new InvalidOperationException($"未设置密钥环境变量 {copy.ApiKeyEnvironmentVariable}。");
        return copy;
    }
}
