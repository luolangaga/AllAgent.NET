using System.Text.Json.Nodes;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using Xunit;

namespace LanAgent.Tests;

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

    private static JsonObject Build(ChatRequest request, bool responses) => responses ? ResponsesProvider.Body(request)
        : JsonNode.Parse(OpenAiProvider.BuildRawRequestBody(request.Config, request, false))!.AsObject();
}
