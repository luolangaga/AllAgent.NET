using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Xunit;
namespace AllAgent.NET.Tests;
public class ModelCatalogTests
{
    [Fact]
    public void DiscoveredLimitsFollowModelAndSurviveSerialization()
    {
        var c = new ApiConfig { ProviderId = "custom", DefaultModel = "small", MaxTokens = 0 };
        ModelCatalog.Remember(c, [new("small", "Small", 8192, 2048), new("large", "Large", 131072, 65536)]);
        c = System.Text.Json.JsonSerializer.Deserialize<ApiConfig>(System.Text.Json.JsonSerializer.Serialize(c))!;
        Assert.Equal(8192, ModelContext.Capacity(c));
        Assert.Equal(2048, ModelCatalog.OutputLimit(c));
        c.DefaultModel = "large";
        Assert.Equal(131072, ModelContext.Capacity(c));
        Assert.Equal(32000, ModelCatalog.OutputLimit(c));
        c.MaxTokens = 100000;
        Assert.Equal(65536, ModelCatalog.OutputLimit(c));
        c.ModelContextWindows["large"] = 200000;
        Assert.Equal(200000, ModelContext.Capacity(c));
    }
    [Fact]
    public void CatalogSupportsOnlyImplementedProtocolsAndExactIds()
    {
        Assert.NotEmpty(ModelCatalog.Providers);
        foreach (var provider in ModelCatalog.Providers) Assert.NotNull(new AiProviderFactory().GetProvider(provider.Id));
        var c = new ApiConfig { ProviderId = "openai", DefaultModel = "gpt-4o", MaxTokens = 0 };
        Assert.True(ModelContext.Capacity(c) > 100000);
        c.DefaultModel = "unknown-gpt-4o-suffix";
        Assert.Null(ModelCatalog.Resolve(c));
        var enriched = ModelCatalog.Enrich(c, new("gpt-4o", "GPT", 64000, 512));
        Assert.Equal(64000, enriched.ContextTokens);
        Assert.Equal(512, enriched.OutputTokens);
    }
    [Theory]
    [InlineData("dashscope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "alibaba-cn")]
    [InlineData("moonshot", "https://api.moonshot.cn/v1", "moonshotai-cn")]
    [InlineData("siliconflow", "https://api.siliconflow.cn/v1", "siliconflow-cn")]
    public void RegionalProvidersUseTheirOwnModelContext(string id, string url, string catalogId)
    {
        var expected = ModelCatalog.List(new ApiConfig { ProviderId = catalogId });
        Assert.NotEmpty(expected);
        var model = expected.First(m => m.ContextTokens >= 1024);
        var config = new ApiConfig { ProviderId = id, BaseUrl = url, DefaultModel = model.Id };
        Assert.Equal(model.ContextTokens, ModelContext.Capacity(config));
        config.ModelContextWindows[model.Id] = 100000;
        config.ModelContextWindows.Remove(model.Id); // /context-size auto
        Assert.Equal(model.ContextTokens, ModelContext.Capacity(config));
    }
    [Fact]
    public void CustomEndpointAndModelSelectTheMatchingCatalog()
    {
        var config = new ApiConfig { ProviderId = "custom", BaseUrl = "https://api.moonshot.cn/v1" };
        var model = ModelCatalog.List(config).First(m => m.ContextTokens >= 1024);
        config.DefaultModel = model.Id;
        Assert.Equal(model.ContextTokens, ModelContext.Capacity(config));
    }
}
