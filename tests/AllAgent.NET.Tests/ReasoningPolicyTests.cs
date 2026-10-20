using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Terminal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ReasoningPolicyTests
{
    [Fact]
    public async Task GatewayCatalogEffortsAreDiscoveredWithoutGuessingUpstreamControls()
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"data":[{"id":"vendor/new-model","reasoning":{"supported_efforts":["low","high"]}},{"id":"fixed","reasoning":false}]}""");
        });
        var config = new ApiConfig { ProviderId = "openrouter", BaseUrl = server.Url, DefaultModel = "vendor/new-model" };
        var models = await ModelDiscovery.ListAsync(config); ModelCatalog.Remember(config, models);
        Assert.Equal(new[] { "default", "low", "high" }, ReasoningSettings.Efforts(config));
        ReasoningSettings.SetEffort(config, "high"); var body = new JsonObject(); ReasoningSettings.Apply(body, config);
        Assert.Equal(true, (bool?)body["provider"]?["require_parameters"]);
        Assert.Equal("high", (string?)body["reasoning"]?["effort"]);
    }

    [Theory]
    [InlineData("openai", "gpt-5", "openai", "default,minimal,low,medium,high")]
    [InlineData("openai", "gpt-5.1", "openai", "default,none,low,medium,high")]
    [InlineData("openai", "gpt-5.2", "openai", "default,none,low,medium,high,xhigh")]
    [InlineData("codex", "gpt-6-astra", "openai", "default,low,medium,high,xhigh,max")]
    [InlineData("openai", "o3-mini", "openai", "default,low,medium,high")]
    [InlineData("deepseek", "deepseek-flash", "deepseek", "default,none,low,high,max")]
    [InlineData("deepseek", "deepseek-v4-pro", "deepseek", "default,none,low,high,max")]
    [InlineData("deepseek", "deepseek-reasoner", "deepseek", "default")]
    [InlineData("anthropic", "claude-sonnet-4-5", "anthropic-budget", "default,none,high,max,budget")]
    [InlineData("anthropic", "claude-opus-4-6", "anthropic-adaptive", "default,low,medium,high,max")]
    [InlineData("anthropic", "claude-opus-4-7", "anthropic-adaptive", "default,low,medium,high,xhigh,max")]
    [InlineData("gemini", "gemini-2.5-pro", "gemini-budget", "default,high,max,budget")]
    [InlineData("gemini", "gemini-2.5-flash", "gemini-budget", "default,none,high,max,budget")]
    [InlineData("gemini", "gemini-3-pro-preview", "gemini-level", "default,low,high")]
    [InlineData("gemini", "gemini-3-flash-preview", "gemini-level", "default,minimal,low,medium,high")]
    [InlineData("dashscope", "qwen3.5-plus", "qwen", "default,none,thinking,budget")]
    [InlineData("workbuddy", "deepseek-v4.1-flash", "workbuddy", "default,none,low,high,max")]
    [InlineData("workbuddy", "glm-5.3", "workbuddy", "default,none,high,max")]
    [InlineData("workbuddy", "kimi-k3", "workbuddy", "default")]
    [InlineData("commandcode", "deepseek/deepseek-v4.1-flash", "commandcode", "default,none,low,high,max")]
    [InlineData("commandcode", "deepseek/deepseek-v4-flash", "commandcode", "default,none,high,max")]
    [InlineData("commandcode", "claude-sonnet-5-5", "commandcode", "default,low,medium,high,xhigh,max")]
    [InlineData("commandcode", "gpt-6-astra", "commandcode", "default,low,medium,high,xhigh,max")]
    [InlineData("commandcode", "gpt-5.5", "commandcode", "default,low,medium,high,xhigh")]
    [InlineData("commandcode", "gpt-5.4-mini", "commandcode", "default,low,medium,high")]
    [InlineData("commandcode", "google/gemini-3.8-flash", "commandcode", "default,low,medium,high")]
    [InlineData("commandcode", "Qwen/Qwen3.8-Max", "commandcode", "default,low,medium,xhigh")]
    [InlineData("commandcode", "zai-org/GLM-5.3", "commandcode", "default,low,high,max")]
    [InlineData("commandcode", "xai/grok-4.6", "commandcode", "default,low,medium,high,xhigh")]
    [InlineData("commandcode", "moonshotai/Kimi-K2.7-Code", "commandcode", "default")]
    [InlineData("xai", "grok-3-mini", "xai", "default,low,high")]
    [InlineData("xai", "grok-4", "xai", "default")]
    public void OptionsFollowNativeModelContracts(string provider, string model, string protocol, string options)
    {
        var config = new ApiConfig { ProviderId = provider, DefaultModel = model };
        Assert.Equal(protocol, ReasoningSettings.Capabilities(config).Protocol);
        Assert.Equal(options, string.Join(',', ReasoningSettings.Efforts(config)));
    }

    [Fact]
    public void OldSavedPresetNamesResolveToTheModelsActualControl()
    {
        Assert.Equal("anthropic-budget", ProviderCatalog.ReasoningFor(new() { ReasoningProtocol = "anthropic-adaptive", DefaultModel = "claude-sonnet-4-5" }));
        Assert.Equal("gemini-budget", ProviderCatalog.ReasoningFor(new() { ReasoningProtocol = "gemini-level", DefaultModel = "gemini-2.5-pro" }));
    }

    [Fact]
    public void DeepSeekTransportAndGatewayUseDifferentRequestShapes()
    {
        foreach (var transport in new[] { "openai", "anthropic", "responses" })
        {
            var config = new ApiConfig { ProviderId = "deepseek", Protocol = transport, DefaultModel = "deepseek-v4-pro" };
            ReasoningSettings.SetEffort(config, "max"); var body = new JsonObject(); ReasoningSettings.Apply(body, config);
            if (transport == "responses") { Assert.Equal("max", (string?)body["reasoning"]?["effort"]); Assert.Null(body["thinking"]); }
            else
            {
                Assert.Equal("enabled", (string?)body["thinking"]?["type"]);
                if (transport == "anthropic") { Assert.Equal("max", (string?)body["output_config"]?["effort"]); Assert.Null(body["reasoning_effort"]); }
                else Assert.Equal("max", (string?)body["reasoning_effort"]);
            }
            Assert.Null(body["budget_tokens"]);
        }
        var routed = new ApiConfig { ProviderId = "openrouter", DefaultModel = "deepseek/deepseek-v4-pro" };
        ReasoningSettings.SetEffort(routed, "high"); var gateway = new JsonObject(); ReasoningSettings.Apply(gateway, routed);
        Assert.Equal("high", (string?)gateway["reasoning"]?["effort"]); Assert.Null(gateway["thinking"]); Assert.Null(gateway["reasoning_effort"]);
    }

    [Fact]
    public void CommandCodeLegacyOpenAiProfileKeepsVendorFamilies()
    {
        var config = new ApiConfig { ProviderId = "commandcode", Protocol = "commandcode", DefaultModel = "deepseek/deepseek-v4.1-flash", ReasoningProtocol = "openai" };
        Assert.Equal("commandcode", ReasoningSettings.Capabilities(config).Protocol);
        Assert.Equal(new[] { "default", "none", "low", "high", "max" }, ReasoningSettings.Efforts(config));
        ModelCatalog.Remember(config, [new("deepseek/deepseek-v4.1-flash", "fixture") { ReasoningLevels = ["none", "max"] }]);
        Assert.Equal(new[] { "default", "none", "max" }, ReasoningSettings.Efforts(config));
        ReasoningSettings.SetEffort(config, "none");
        var body = new JsonObject(); ReasoningSettings.Apply(body, config);
        Assert.Equal("off", (string?)body["reasoning_effort"]);
    }

    [Fact]
    public void WorkBuddyLegacyNoneProtocolUpgradesToNativeEffort()
    {
        var config = new ApiConfig { ProviderId = "workbuddy", DefaultModel = "deepseek-v4.1-flash", ReasoningProtocol = "none" };
        Assert.Equal("workbuddy", ReasoningSettings.Capabilities(config).Protocol);
        Assert.Contains("max", ReasoningSettings.Efforts(config));
        ReasoningSettings.SetEffort(config, "max");
        var body = new JsonObject(); ReasoningSettings.Apply(body, config);
        Assert.Equal("max", (string?)body["reasoning_effort"]);
        ReasoningSettings.SetEffort(config, "none");
        body = new(); ReasoningSettings.Apply(body, config);
        Assert.Equal("none", (string?)body["reasoning_effort"]);
    }

    [Fact]
    public void NativeBudgetsRespectOutputLimitAndCannotPartiallyChangeSettings()
    {
        var config = new ApiConfig { ProviderId = "anthropic", DefaultModel = "claude-sonnet-4-5", MaxTokens = 8192 };
        ReasoningSettings.SetEffort(config, "high"); var body = new JsonObject(); ReasoningSettings.Apply(body, config);
        Assert.Equal(8191, (int?)body["thinking"]?["budget_tokens"]);
        Assert.Null(body["output_config"]);
        Assert.Throws<ArgumentException>(() => ReasoningSettings.Configure(config, "budget", 8192));
        Assert.Equal("high", config.ReasoningEffort); Assert.Equal(8000, config.ThinkingBudget);
        ReasoningSettings.Configure(config, "budget", 4096);
        Assert.Contains("4096 tokens", ReasoningSettings.Label(config));
        var deepseek = new ApiConfig { ProviderId = "deepseek", DefaultModel = "deepseek-flash" };
        Assert.Throws<ArgumentException>(() => ReasoningSettings.Configure(deepseek, "high", 8000));
        Assert.Throws<ArgumentException>(() => ReasoningSettings.SetEffort(deepseek, "medium"));
        Assert.Equal("default", deepseek.ReasoningEffort);
    }

    [Fact]
    public void GeminiBudgetAndLevelNeverMixAndDefaultStillRequestsThoughts()
    {
        var config = new ApiConfig { ProviderId = "gemini", DefaultModel = "gemini-2.5-pro" };
        ReasoningSettings.Configure(config, "budget", 128); var body = new JsonObject(); ReasoningSettings.Apply(body, config);
        Assert.Equal(128, (int?)body["generationConfig"]?["thinkingConfig"]?["thinkingBudget"]);
        Assert.Null(body["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
        Assert.Throws<ArgumentException>(() => ReasoningSettings.SetEffort(config, "none"));
        config.DefaultModel = "gemini-3-pro-preview"; ReasoningSettings.RestoreForModel(config);
        body = new(); ReasoningSettings.Apply(body, config);
        Assert.True((bool?)body["generationConfig"]?["thinkingConfig"]?["includeThoughts"]);
        ReasoningSettings.SetEffort(config, "low"); body = new(); ReasoningSettings.Apply(body, config);
        Assert.Equal("low", (string?)body["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
        Assert.Null(body["generationConfig"]?["thinkingConfig"]?["thinkingBudget"]);
    }

    [Fact]
    public async Task ModelEffortAndBudgetSurviveSwitchesStoreReopenAndChildOverrides()
    {
        using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "data"); Guid id;
        using (var store = new DataStore(path))
        {
            var config = new ApiConfig { ProviderId = "gemini", DefaultModel = "gemini-2.5-pro", Models = ["gemini-2.5-pro", "gemini-3-pro-preview"], ApiKey = "fixture" };
            ReasoningSettings.Configure(config, "budget", 4096); store.AddApiConfig(config); id = config.Id;
            var manager = new ModelManager(store, new TerminalUi(true, false), TerminalArguments.Parse([]));
            var other = await manager.SelectAsync(id.ToString(), "gemini-3-pro-preview"); Assert.Equal("default", other.ReasoningEffort);
            ReasoningSettings.SetEffort(other, "low"); store.UpdateApiConfig(other);
        }
        using (var store = new DataStore(path))
        {
            var manager = new ModelManager(store, new TerminalUi(true, false), TerminalArguments.Parse([]));
            var first = await manager.SelectAsync(id.ToString(), "gemini-2.5-pro");
            Assert.Equal("budget", first.ReasoningEffort); Assert.Equal(4096, first.ThinkingBudget);
            var child = SubAgentModelSelection.Resolve(first, [first], "gemini-3-pro-preview");
            Assert.Equal("low", child.ReasoningEffort); Assert.Equal("budget", first.ReasoningEffort);
        }
    }

    [Fact]
    public void EndpointAdvertisedLevelsOverrideFamilyFallbackAndPersist()
    {
        var config = new ApiConfig { ProviderId = "codex", DefaultModel = "gpt-6-astra" };
        ModelCatalog.Remember(config, [new("gpt-6-astra", "fixture") { ReasoningLevels = ["low", "high", "ultra"] }]);
        config = JsonSerializer.Deserialize<ApiConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(new[] { "default", "low", "high", "ultra" }, ReasoningSettings.Efforts(config));
        Assert.Throws<ArgumentException>(() => ReasoningSettings.SetEffort(config, "medium"));
        ReasoningSettings.SetEffort(config, "ultra");
        Assert.Equal("ultra", (string?)ResponsesProvider.Body(new(config, []))["reasoning"]?["effort"]);
        ModelCatalog.Remember(config, [new("gpt-6-astra", "fixture") { ReasoningLevels = [] }]);
        Assert.Equal(new[] { "default" }, ReasoningSettings.Efforts(config));
    }
}
