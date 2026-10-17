using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class SubAgentModelSelectionTests
{
    [Fact]
    public void SelectionInheritsAnIndependentCopyAndRetainsAuthentication()
    {
        var parent = new ApiConfig { ProviderId = "openai", DefaultModel = "current", AuthenticationContextId = "auth", Models = ["current"], ModelContextWindows = new() { ["current"] = 32000 } };
        var child = SubAgentModelSelection.Resolve(parent, [parent]);
        Assert.Equal(parent.DefaultModel, child.DefaultModel);
        Assert.Equal("auth", child.AuthenticationContextId);
        child.Models.Add("extra"); child.ModelContextWindows["current"] = 8000;
        Assert.Single(parent.Models); Assert.Equal(32000, parent.ModelContextWindows["current"]);
        var overridden = SubAgentModelSelection.Resolve(parent, [parent], "other");
        Assert.Equal("other", overridden.DefaultModel); Assert.Equal("current", parent.DefaultModel);
    }

    [Fact]
    public void SelectionUsesConfiguredProviderAndRejectsDisabledOrAmbiguousSelectors()
    {
        var parent = new ApiConfig { ProviderId = "openai", DefaultModel = "current" };
        var other = new ApiConfig { ProviderId = "anthropic", DisplayName = "Research", DefaultModel = "research", ApiKey = "other-key" };
        var selected = SubAgentModelSelection.Resolve(parent, [parent, other], provider: "Research");
        Assert.Equal("research", selected.DefaultModel); Assert.Equal("other-key", selected.ApiKey);
        Assert.Equal(other.Id, SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString()[..8]).Id);
        Assert.Equal(other.Id, SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString("N")).Id);
        Assert.Equal("current", parent.DefaultModel);
        Assert.Throws<ArgumentException>(() => SubAgentModelSelection.Resolve(parent, [other, new ApiConfig { ProviderId = "anthropic" }], provider: "anthropic"));
        other.IsEnabled = false;
        Assert.Throws<ArgumentException>(() => SubAgentModelSelection.Resolve(parent, [other], provider: other.Id.ToString()));
    }
}
