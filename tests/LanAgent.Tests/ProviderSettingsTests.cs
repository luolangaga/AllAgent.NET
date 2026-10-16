using System.Text.Json;
using System.Text.Json.Nodes;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LanAgent.Tests;

public sealed class ProviderSettingsTests
{
    [Theory]
    [InlineData("openai", "high", "reasoning_effort")]
    [InlineData("deepseek", "high", "thinking")]
    [InlineData("qwen", "budget", "enable_thinking")]
    [InlineData("openrouter", "low", "reasoning")]
    [InlineData("anthropic-adaptive", "high", "output_config")]
    [InlineData("anthropic-budget", "budget", "thinking")]
    [InlineData("gemini-level", "low", "generationConfig")]
    [InlineData("gemini-budget", "budget", "generationConfig")]
    public void ReasoningUsesProviderSpecificFields(string protocol, string effort, string field)
    {
        var config = new ApiConfig { ReasoningProtocol = protocol, ThinkingBudget = 2048, MaxTokens = 8192 };
        ReasoningSettings.SetEffort(config, effort);
        var body = new JsonObject { ["temperature"] = 0.7, ["max_tokens"] = 8192, ["generationConfig"] = new JsonObject() };
        ReasoningSettings.Apply(body, config); Assert.NotNull(body[field]);
        if (protocol == "openai") { Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>()); Assert.Null(body["thinking"]); Assert.Null(body["max_tokens"]); Assert.Equal(8192, body["max_completion_tokens"]!.GetValue<int>()); }
        if (protocol == "anthropic-adaptive") Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        if (protocol == "gemini-level") Assert.Equal("low", body["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
    }
    [Fact]
    public void UnsupportedEffortIsRejectedAndDefaultDoesNotEmitThinking()
    {
        var config = new ApiConfig { ProviderId = "custom", ReasoningProtocol = "none" };
        Assert.Throws<ArgumentException>(() => ReasoningSettings.SetEffort(config, "high"));
        var body = new JsonObject(); ReasoningSettings.Apply(body, config); Assert.Empty(body);
        config.ReasoningProtocol = "anthropic-budget"; config.ThinkingBudget = 8192; config.MaxTokens = 4096;
        Assert.Throws<ArgumentException>(() => ReasoningSettings.SetEffort(config, "budget"));
    }
    [Theory]
    [InlineData("openai", "/models", "Authorization", "Bearer fixture-key", "{\"data\":[{\"id\":\"model-a\"},{\"id\":\"model-b\"}]}")]
    [InlineData("anthropic", "/v1/models", "x-api-key", "fixture-key", "{\"data\":[{\"id\":\"model-a\",\"display_name\":\"Model A\"}],\"has_more\":false}")]
    [InlineData("gemini", "/v1beta/models", "x-goog-api-key", "fixture-key", "{\"models\":[{\"name\":\"models/model-a\",\"displayName\":\"Model A\"}]}")]
    public async Task ModelDiscoveryUsesRealEndpointHeadersAndServerModels(string protocol, string path, string header, string expected, string json)
    {
        await using var server = await LocalApiServer.StartAsync(async context => { Assert.Equal(path, context.Request.Path.Value); Assert.Equal(expected, context.Request.Headers[header].ToString()); context.Response.ContentType = "application/json"; await context.Response.WriteAsync(json); });
        var config = new ApiConfig { ProviderId = "custom", Protocol = protocol, BaseUrl = server.Url, ApiKey = "fixture-key" };
        var models = await ModelDiscovery.ListAsync(config); Assert.Equal("model-a", models[0].Id);
    }
    [Fact]
    public async Task FailedModelDiscoveryPreservesSavedManualList()
    {
        await using var server = await LocalApiServer.StartAsync(context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        var config = new ApiConfig { ProviderId = "custom", Protocol = "openai", BaseUrl = server.Url, Models = new() { "manual" } };
        Assert.Contains("401", (await Assert.ThrowsAsync<InvalidOperationException>(() => ModelDiscovery.ListAsync(config))).Message); Assert.Equal("manual", Assert.Single(config.Models));
    }
    [Fact]
    public async Task OpenAiEffortReachesActualStreamRequest()
    {
        JsonElement captured = default;
        await using var server = await LocalApiServer.StartAsync(async context => { using var json = await JsonDocument.ParseAsync(context.Request.Body); captured = json.RootElement.Clone(); context.Response.ContentType = "text/event-stream"; await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"); });
        var config = new ApiConfig { ProviderId = "custom", Protocol = "openai", BaseUrl = server.Url, ReasoningProtocol = "openai", ReasoningEffort = "high", DefaultModel = "reasoning-model" };
        await foreach (var _ in new AiProviderFactory().GetProvider(config).StreamChatAsync(new(config, new[] { new ChatMessage("user", "test") }))) { }
        Assert.Equal("high", captured.GetProperty("reasoning_effort").GetString()); Assert.False(captured.TryGetProperty("thinking", out _)); Assert.False(captured.TryGetProperty("temperature", out _));
    }
    [Fact]
    public async Task AnthropicThinkingSignaturesSurviveToolRoundTrip()
    {
        var requestCount = 0; JsonElement second = default;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body); if (++requestCount == 2) second = request.RootElement.Clone();
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: content_block_start\ndata: {\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\nevent: content_block_delta\ndata: {\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"thought\"}}\n\nevent: content_block_delta\ndata: {\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"signed-proof\"}}\n\nevent: content_block_stop\ndata: {\"index\":0}\n\nevent: message_stop\ndata: {}\n\n");
        });
        var config = new ApiConfig { ProviderId = "anthropic", BaseUrl = server.Url, ApiKey = "fixture", DefaultModel = "fixture" };
        string? state = null;
        await foreach (var chunk in new AnthropicProvider().StreamChatAsync(new(config, new[] { new ChatMessage("user", "task") }))) if (chunk.Type == ChunkType.ProviderState) state = chunk.ProviderStateJson;
        Assert.Contains("signed-proof", state);
        await foreach (var _ in new AnthropicProvider().StreamChatAsync(new(config, new[] { new ChatMessage("user", "task"), new ChatMessage("assistant", "", ProviderStateJson: state), new ChatMessage("user", "next") }))) { }
        Assert.Equal("signed-proof", second.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("signature").GetString());
    }
}
