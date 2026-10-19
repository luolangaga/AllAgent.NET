using System.Text.Json;
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

    [Fact]
    public void legacy_small_output_limits_follow_the_model_catalog_once()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        Directory.CreateDirectory(data);
        var legacy = new[]
        {
            new ApiConfig { ProviderId = "custom", DisplayName = "web-default", BaseUrl = "https://example.test/v1", DefaultModel = "test-model", MaxTokens = 4096 },
            new ApiConfig { ProviderId = "custom", DisplayName = "preset-default", BaseUrl = "https://example.test/v1", DefaultModel = "test-model", MaxTokens = 16384 },
            new ApiConfig { ProviderId = "custom", DisplayName = "small", BaseUrl = "https://example.test/v1", DefaultModel = "test-model", MaxTokens = 8192 },
            new ApiConfig { ProviderId = "custom", DisplayName = "chosen", BaseUrl = "https://example.test/v1", DefaultModel = "test-model", MaxTokens = 32768 },
        };
        File.WriteAllText(Path.Combine(data, "allagent.json"), JsonSerializer.Serialize(new { version = 3, apiConfigs = legacy }));

        using (var store = new DataStore(data))
        {
            Assert.Equal(0, store.ApiConfigs.Single(item => item.DisplayName == "web-default").MaxTokens);
            Assert.Equal(0, store.ApiConfigs.Single(item => item.DisplayName == "preset-default").MaxTokens);
            Assert.Equal(0, store.ApiConfigs.Single(item => item.DisplayName == "small").MaxTokens);
            Assert.Equal(32768, store.ApiConfigs.Single(item => item.DisplayName == "chosen").MaxTokens);
            // The applied floor is persisted with the next save, so a deliberate cap set afterwards stays.
            var chosen = store.ApiConfigs.Single(item => item.DisplayName == "chosen");
            chosen.MaxTokens = 16384;
            store.UpdateApiConfig(chosen);
        }

        using var reopened = new DataStore(data);
        Assert.Equal(16384, reopened.ApiConfigs.Single(item => item.DisplayName == "chosen").MaxTokens);
        Assert.Equal(0, reopened.ApiConfigs.Single(item => item.DisplayName == "web-default").MaxTokens);
    }
}