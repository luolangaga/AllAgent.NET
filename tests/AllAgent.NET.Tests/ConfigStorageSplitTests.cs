using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>
/// Configuration is global: providers, models, skills, MCP servers, settings and usage live in
/// one shared manifest, so switching workspaces only switches conversations.
/// </summary>
public sealed class ConfigStorageSplitTests
{
    private static ApiConfig Provider(string name) => new()
    {
        ProviderId = "custom",
        DisplayName = name,
        BaseUrl = "https://example.test/v1",
        ApiKey = "test-key",
        DefaultModel = "test-model",
    };

    [Fact]
    public void keeps_configuration_global_and_sessions_per_workspace()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");

        using (var store = new DataStore(data, false, shared))
        {
            store.AddApiConfig(Provider("config-provider"));
            store.SetSetting("theme", "dark");
            store.AddConversation(new Conversation { Title = "workspace-session" });
        }

        var sharedJson = File.ReadAllText(Path.Combine(shared, "allagent.json"));
        Assert.Contains("config-provider", sharedJson);
        Assert.DoesNotContain("workspace-session", sharedJson);

        var workspaceJson = File.ReadAllText(Path.Combine(data, "allagent.json"));
        Assert.DoesNotContain("config-provider", workspaceJson);

        using var reopened = new DataStore(data, false, shared);
        Assert.Equal("config-provider", Assert.Single(reopened.ApiConfigs).DisplayName);
        Assert.Equal("dark", reopened.GetSetting("theme"));
        Assert.Equal("workspace-session", Assert.Single(reopened.Conversations).Title);
    }

    [Fact]
    public void seeds_the_shared_manifest_from_a_workspace_that_only_has_local_config()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");

        using (var legacy = new DataStore(data)) legacy.AddApiConfig(Provider("local-provider"));

        using (var store = new DataStore(data, false, shared))
            Assert.Equal("local-provider", Assert.Single(store.ApiConfigs).DisplayName);

        Assert.Contains("local-provider", File.ReadAllText(Path.Combine(shared, "allagent.json")));
    }

    [Fact]
    public void reloading_the_workspace_manifest_keeps_the_shared_configuration()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");
        using var store = new DataStore(data, false, shared);
        store.AddApiConfig(Provider("reload-provider"));
        store.AddConversation(new Conversation { Title = "session" });

        // Every write transaction refreshes from disk; the config-free workspace manifest
        // must not clobber the configuration the picker and the streaming start rely on.
        store.Refresh();
        store.SetSetting("theme", "dark");
        store.Refresh();

        var config = Assert.Single(store.ApiConfigs);
        Assert.Equal("reload-provider", config.DisplayName);
        Assert.NotNull(store.GetApiConfig(config.Id));
        Assert.Equal("dark", store.GetSetting("theme"));
    }

    [Fact]
    public void shares_one_configuration_without_sharing_sessions()
    {
        using var first = new TestWorkspace();
        using var second = new TestWorkspace();
        var shared = Path.Combine(first.Root, "shared");

        using (var store = new DataStore(Path.Combine(first.Root, "data"), false, shared))
        {
            store.AddApiConfig(Provider("shared-provider"));
            store.AddConversation(new Conversation { Title = "first-only" });
        }

        using var other = new DataStore(Path.Combine(second.Root, "data"), false, shared);
        Assert.Equal("shared-provider", Assert.Single(other.ApiConfigs).DisplayName);
        Assert.Empty(other.Conversations);
    }
}