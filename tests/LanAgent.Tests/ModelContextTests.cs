using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Terminal;
using Microsoft.AspNetCore.Http;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ModelContextTests
{
    [Fact]
    public void CodexPresetIsAvailableOfflineAndManualCapacityWinsOverDiscovery()
    {
        var config = new ApiConfig { ProviderId = "codex", DefaultModel = "gpt-6.1-sol" };
        Assert.Equal(1050000, ModelContext.Capacity(config));
        config.ModelLimits[config.DefaultModel] = new(1000000, 128000);
        config.ModelContextWindows[config.DefaultModel] = 500000;
        Assert.Equal(500000, ModelContext.Capacity(config));
    }
    [Theory]
    [InlineData("100k", 100000)]
    [InlineData("200k", 200000)]
    [InlineData("400k", 400000)]
    [InlineData("1M", 1000000)]
    [InlineData("131072", 131072)]
    [InlineData("1.5m", 1500000)]
    public void PresetsAndCustomValuesUseTokens(string text, int value) => Assert.Equal(value, ModelContext.Parse(text));

    [Fact]
    public async Task CapacityIsSavedPerModelAndFooterDistinguishesMeasuredUsage()
    {
        using var workspace = new TestWorkspace(); var directory = Path.Combine(workspace.Root, "data"); using var store = new DataStore(directory);
        var config = new ApiConfig { DisplayName = "fixture", DefaultModel = "model-A", Models = ["model-A", "model-B"] }; store.AddApiConfig(config);
        var ui = new TerminalUi(false, false); var manager = new ModelManager(store, ui, TerminalArguments.Parse([]));
        await manager.SetContextAsync(config, "400k"); config.DefaultModel = "model-B"; await manager.SetContextAsync(config, "1M");
        using var reopened = new DataStore(directory); var saved = Assert.Single(reopened.ApiConfigs);
        Assert.Equal(400000, saved.ModelContextWindows["model-A"]); Assert.Equal(1000000, saved.ModelContextWindows["model-B"]);
        Assert.Equal(1000000, ModelContext.Capacity(config));
        await ui.RenderAsync(new AgentContextUsage(32000, 1000000, false), default);
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 30;
        using var screen = new FullScreenTerminal(store, catalog, mcp, ui, console); console.Write(screen.BuildFrame(100, 30));
        Assert.Contains("上下文 32k / 1000k", console.Output);
        await ui.RenderAsync(new AgentContextUsage(32500, 1000000, true), default); Assert.Contains("≈32.5k", ui.ContextLabel);
        Assert.Throws<ArgumentException>(() => ModelContext.Parse("-1M")); Assert.Throws<ArgumentException>(() => ModelContext.Parse("garbage"));
        config.DefaultModel = "model-A"; ui.SetModelContext(config); Assert.Equal(400000, ui.ContextCapacityTokens);
    }

    [Theory]
    [InlineData("openai", 32000, 1200)]
    [InlineData("anthropic", 32000, 1200)]
    [InlineData("gemini", 32000, 1200)]
    public async Task ProvidersReadActualTokenUsageIncludingCachedInputs(string protocol, int expectedInput, int expectedOutput)
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(protocol switch
            {
                "openai" => "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":32000,\"completion_tokens\":1200}}\n\ndata: [DONE]\n\n",
                "anthropic" => "event: message_start\ndata: {\"message\":{\"usage\":{\"input_tokens\":1000,\"cache_read_input_tokens\":30000,\"cache_creation_input_tokens\":1000}}}\n\nevent: message_delta\ndata: {\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1200}}\n\nevent: message_stop\ndata: {}\n\n",
                _ => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":32000,\"totalTokenCount\":33200}}\n\n"
            });
        });
        var config = new ApiConfig { ProviderId = protocol, BaseUrl = server.Url, DefaultModel = "fixture" };
        var usages = new List<ModelTokenUsage>();
        await foreach (var chunk in new AiProviderFactory().GetProvider(config).StreamChatAsync(new(config, [new("user", "task")]))) if (chunk.Usage != null) usages.Add(chunk.Usage);
        Assert.Equal(expectedInput, usages.Last().InputTokens); Assert.Equal(expectedOutput, usages.Last().OutputTokens);
        if (protocol == "anthropic") Assert.Equal(30000, usages.Last().CachedInputTokens);
    }
}
