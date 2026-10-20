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
    public void live_workspaces_share_configuration_without_reloading()
    {
        using var first = new TestWorkspace();
        using var second = new TestWorkspace();
        var shared = Path.Combine(first.Root, "shared");
        var firstStore = new DataStore(Path.Combine(first.Root, "data"), false, shared);
        var secondStore = new DataStore(Path.Combine(second.Root, "data"), false, shared);

        // The second workspace was already open before the login happened.
        firstStore.AddApiConfig(Provider("commandcode-login"));
        Assert.Equal("commandcode-login", Assert.Single(secondStore.ApiConfigs).DisplayName);

        // A write from the other workspace must merge into the same live configuration
        // instead of saving its stale snapshot over the login.
        secondStore.SetSetting("theme", "dark");
        secondStore.AddApiConfig(Provider("second-provider"));
        Assert.Equal(2, firstStore.ApiConfigs.Count);
        Assert.Equal("dark", firstStore.GetSetting("theme"));

        // Disposing one workspace must not dispose the configuration lock the others use.
        firstStore.Dispose();
        secondStore.AddApiConfig(Provider("after-dispose"));
        Assert.Equal(3, secondStore.ApiConfigs.Count);
        secondStore.Dispose();

        using var reopened = new DataStore(Path.Combine(second.Root, "data"), false, shared);
        Assert.Equal(3, reopened.ApiConfigs.Count);
    }

    [Fact]
    public void does_not_create_a_shared_manifest_from_an_empty_workspace()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "allagent.json"), JsonSerializer.Serialize(new { version = 3 }));

        using var store = new DataStore(data, false, shared);

        Assert.Empty(store.ApiConfigs);
        // A missing shared file can be a transient state (another process replacing it).
        // Startup must never manufacture an empty manifest that then hides the real one.
        Assert.False(File.Exists(Path.Combine(shared, "allagent.json")));
    }

    [Fact]
    public void recovers_the_shared_manifest_from_backup_when_main_is_missing()
    {
        using var workspace = new TestWorkspace();
        var shared = Path.Combine(workspace.Root, "shared");
        var main = Path.Combine(shared, "allagent.json");
        var backup = main + ".bak";
        using (var store = new DataStore(Path.Combine(workspace.Root, "first"), false, shared))
            store.AddApiConfig(Provider("recovered-provider"));

        File.Move(main, backup, overwrite: true);
        File.Delete(main);

        using var recovered = new DataStore(Path.Combine(workspace.Root, "second"), false, shared);

        Assert.Equal("recovered-provider", Assert.Single(recovered.ApiConfigs).DisplayName);
        Assert.True(File.Exists(main));
    }

    [Fact]
    public void refuses_to_replace_an_unreadable_shared_manifest()
    {
        using var workspace = new TestWorkspace();
        var shared = Path.Combine(workspace.Root, "shared");
        Directory.CreateDirectory(shared);
        var main = Path.Combine(shared, "allagent.json");
        File.WriteAllText(main, "{ not valid json");

        Assert.Throws<InvalidDataException>(() => new DataStore(Path.Combine(workspace.Root, "data"), false, shared));
        Assert.Equal("{ not valid json", File.ReadAllText(main));
    }

    [Fact]
    public void seeds_the_shared_manifest_from_a_legacy_workspace_login_behind_an_empty_manifest()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");
        Directory.CreateDirectory(data);
        // The current manifest exists but carries no configuration (the workspace layout
        // keeps providers in the shared file); the pre-split file still has the login.
        File.WriteAllText(Path.Combine(data, "allagent.json"), JsonSerializer.Serialize(new { version = 3 }));
        File.WriteAllText(Path.Combine(data, "lanagent.json"), JsonSerializer.Serialize(new
        {
            version = 3,
            apiConfigs = new[] { Provider("legacy-provider") },
            appSettings = new Dictionary<string, string> { ["terminal:lastConfig"] = "legacy-config" },
        }));

        using var store = new DataStore(data, false, shared);

        Assert.Equal("legacy-provider", Assert.Single(store.ApiConfigs).DisplayName);
        Assert.Equal("legacy-config", store.GetSetting("terminal:lastConfig"));
        var sharedJson = File.ReadAllText(Path.Combine(shared, "allagent.json"));
        Assert.Contains("legacy-provider", sharedJson);
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