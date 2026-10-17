using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PromptCacheRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfficialRequestsKeepAffinityAcrossTurnsAndSeparateSessions(bool responses)
    {
        var config = new ApiConfig { ProviderId = "openai", BaseUrl = "https://api.openai.com/v1", DefaultModel = "fixture" };
        var session = Guid.NewGuid();
        JsonObject Body(Guid id) => Build(new(config, [new("user", "fixture")]) { ConversationId = id, RoutingTurnId = Guid.NewGuid() }, responses);
        var first = Body(session)["prompt_cache_key"]!.GetValue<string>();
        Assert.Equal(first, Body(session)["prompt_cache_key"]!.GetValue<string>());
        Assert.NotEqual(first, Body(Guid.NewGuid())["prompt_cache_key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://proxy.example/v1")]
    [InlineData("https://api.openai.com.example/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void CompatibleEndpointsDoNotReceiveUnsupportedFields(string url)
    {
        var request = new ChatRequest(new ApiConfig { ProviderId = "openai", BaseUrl = url, DefaultModel = "fixture" }, [new("user", "fixture")]) { ConversationId = Guid.NewGuid() };
        Assert.Null(Build(request, false)["prompt_cache_key"]);
        Assert.Null(Build(request, true)["prompt_cache_key"]);
    }

    [Fact]
    public void RequestsWithoutSessionsDoNotGenerateRandomKeys()
    {
        var request = new ChatRequest(new ApiConfig { ProviderId = "openai", BaseUrl = "https://api.openai.com/v1", DefaultModel = "fixture" }, []);
        Assert.Null(Build(request, false)["prompt_cache_key"]);
        Assert.Null(Build(request, true)["prompt_cache_key"]);
    }

    [Theory]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5.1-codex", true)]
    [InlineData("gpt-5.5", true)]
    [InlineData("gpt-5.6", false)]
    [InlineData("gpt-6.1-sol", false)]
    [InlineData("o3", false)]
    public void ExtendedRetentionIsLimitedToSupportedModels(string model, bool expected)
    {
        var request = new ChatRequest(new ApiConfig { ProviderId = "openai", BaseUrl = "https://api.openai.com/v1", DefaultModel = model }, [new("user", "fixture")])
        { PromptCacheRetention = "24h" };
        Assert.Equal(expected, Build(request, false)["prompt_cache_retention"] is not null);
        Assert.Equal(expected, Build(request, true)["prompt_cache_retention"] is not null);
    }

    [Theory]
    [InlineData("https://proxy.example/v1")]
    [InlineData("https://api.openai.com.example/v1")]
    [InlineData("http://api.openai.com/v1")]
    public void ExtendedRetentionStaysOffNonOfficialEndpoints(string url)
    {
        var request = new ChatRequest(new ApiConfig { ProviderId = "openai", BaseUrl = url, DefaultModel = "gpt-5.1" }, [new("user", "fixture")])
        { PromptCacheRetention = "24h" };
        Assert.Null(Build(request, false)["prompt_cache_retention"]);
        Assert.Null(Build(request, true)["prompt_cache_retention"]);
    }

    [Fact]
    public void ExtendedRetentionRequiresExplicitOptIn()
    {
        var request = new ChatRequest(new ApiConfig { ProviderId = "openai", BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-5.1" }, [new("user", "fixture")]);
        Assert.Null(Build(request, false)["prompt_cache_retention"]);
        Assert.Null(Build(request, true)["prompt_cache_retention"]);
    }

    private static JsonObject Build(ChatRequest request, bool responses) => responses ? ResponsesProvider.Body(request)
        : JsonNode.Parse(OpenAiProvider.BuildRawRequestBody(request.Config, request, false))!.AsObject();
}
