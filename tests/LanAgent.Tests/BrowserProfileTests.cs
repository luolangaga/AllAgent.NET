using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class BrowserProfileTests
{
    [Fact]
    public void ManualProfileSelectionSurvivesRestartAndCancelledConnectionWithoutOverwritingVerifiedSettings()
    {
        using var workspace = new TestWorkspace();
        var selectedPath = Path.Combine(workspace.Root, "custom-browser-data", "Profile 3");
        Directory.CreateDirectory(selectedPath); File.WriteAllText(Path.Combine(selectedPath, "Preferences"), "{}");
        var data = Path.Combine(workspace.Root, "data");
        var verified = new BrowserUseConfiguration(Mode: "chromium", CdpUrl: "http://127.0.0.1:9222")
            { BrowserName = "Microsoft Edge", ProfileDirectory = "previous-verified-profile", TabId = "previous-session-tab" };
        using (var store = new DataStore(data))
        {
            var state = new BrowserUseState(); state.Save(store, verified);
            BrowserUseManager.RememberProfile(store, "Microsoft Edge", Environment.ProcessPath!, BrowserProfiles.FromPath(selectedPath));
            Assert.Equal(verified, BrowserUseState.Read(store)); // No successful connection yet.
        }
        using var reopened = new DataStore(data);
        var restored = new BrowserUseState(); restored.Load(reopened);
        Assert.Equal(verified, restored.Snapshot().Configuration);
        Assert.Equal(selectedPath, BrowserUseManager.SavedProfile(reopened, restored.Snapshot().Configuration, "Microsoft Edge")!.Path);
        Assert.Equal(Environment.ProcessPath, reopened.GetSetting("browser:last-executable:Microsoft Edge"));
        Assert.Null(BrowserUseManager.SavedProfile(reopened, restored.Snapshot().Configuration, "Google Chrome"));
        var skill = new SkillCatalog(reopened, workspace.Root, data, workspace.Root).Get("browser-use", true);
        Assert.Contains("首次连接浏览器", skill.Instructions);
        Assert.Contains("/browser reconnect", skill.Instructions);
    }

    [Fact]
    public void PreviouslyVerifiedProfileIsRestoredWithoutNewSelectionMetadata()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "Default"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Preferences"), "{}");
        var data = Path.Combine(workspace.Root, "data");
        using (var store = new DataStore(data))
            new BrowserUseState().Save(store, new(Mode: "chromium", CdpUrl: "http://127.0.0.1:9222") { BrowserName = "Microsoft Edge", ProfileDirectory = path });
        using var reopened = new DataStore(data);
        Assert.Equal(path, BrowserUseManager.SavedProfile(reopened, BrowserUseState.Read(reopened), "Microsoft Edge")!.Path);
    }

    [Fact]
    public void EnumeratesExistingNamedProfilesAndLaunchesOnlySelectedDirectory()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "User Data");
        Directory.CreateDirectory(Path.Combine(root, "Default"));
        Directory.CreateDirectory(Path.Combine(root, "Profile 2"));
        Directory.CreateDirectory(Path.Combine(root, "ShaderCache"));
        File.WriteAllText(Path.Combine(root, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object>
            { ["Default"] = new { name = "个人" }, ["Profile 2"] = new { name = "工作" } } } }));
        File.WriteAllText(Path.Combine(root, "Profile 2", "Preferences"), "{}");
        var profiles = BrowserProfiles.Read(root);
        Assert.Equal(2, profiles.Count);
        var selected = BrowserProfiles.FromPath(Path.Combine(root, "Profile 2"));
        Assert.Equal("工作", selected.Name);
        var launch = BrowserUseManager.ExistingProfileStartInfo(Environment.ProcessPath!, selected, "edge", "about:blank#selected");
        Assert.Contains("--user-data-dir=" + root, launch.ArgumentList);
        Assert.Contains("--profile-directory=Profile 2", launch.ArgumentList);
        Assert.Contains("edge://inspect/#remote-debugging", launch.ArgumentList);
        Assert.DoesNotContain(launch.ArgumentList, arg => arg.Contains("extension") || arg.StartsWith("--remote-debugging-port"));
        Assert.Throws<ArgumentException>(() => BrowserProfiles.FromPath(root));
        Assert.Throws<ArgumentException>(() => BrowserProfiles.FromPath(Path.Combine(root, "missing")));
        Assert.False(Directory.Exists(Path.Combine(root, "missing")));
    }

    [Fact]
    public void DiscoversLocalSocketAndFallsBackWhenProfileMetadataIsIncomplete()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.Root;
        Directory.CreateDirectory(Path.Combine(root, "Default"));
        File.WriteAllText(Path.Combine(root, "Default", "Preferences"), "{}");
        File.WriteAllText(Path.Combine(root, "Local State"), "{partial write");
        Assert.Single(BrowserProfiles.Read(root));
        var active = Path.Combine(root, "DevToolsActivePort");
        Assert.Null(BrowserProfiles.DebugEndpoint(root));
        File.WriteAllText(active, "9222\n/devtools/browser/test-id\n");
        Assert.Equal("ws://127.0.0.1:9222/devtools/browser/test-id", BrowserProfiles.DebugEndpoint(root));
        File.WriteAllText(active, "9222\nws://example.com\n");
        Assert.Null(BrowserProfiles.DebugEndpoint(root));
    }

    [Theory]
    [InlineData("builtin", "chromium")]
    [InlineData("chromium", "builtin")]
    public async Task ModelCanOverrideSavedModeAndReceivesRequestedModeDependencyError(string saved, string requested)
    {
        var state = new BrowserUseState { WorkerPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") }; state.Configure(new(Mode: saved));
        await using var tools = new BrowserUseTools(state);
        var result = await tools.OpenAsync(mode: requested);
        Assert.False(result.Ok);
        Assert.Contains(requested == "chromium" ? "manual connection" : OperatingSystem.IsWindows() ? "worker is missing" : "requires Windows", result.Error);
        Assert.Equal(saved, state.Snapshot().Configuration.Mode);
    }
}
