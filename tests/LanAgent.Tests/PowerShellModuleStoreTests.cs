using System.Text.Json;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class PowerShellModuleStoreTests
{
    [Fact]
    public void ModuleDiscoveryOrderDoesNotChangePromptSummary()
    {
        using var w = new TestWorkspace();
        var store = new PowerShellModuleStore(w.Root, w.Pwsh);
        var directory = Path.Combine(w.Root, "powershell");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "capabilities.json");
        var modules = new[]
        {
            new { name = "ZModule", version = "1.0", commands = new[] { "Get-Z" }, error = (string?)null },
            new { name = "AModule", version = "2.0", commands = new[] { "Get-A" }, error = (string?)null }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(modules));
        store.LoadCatalog(); var first = store.Summary;
        File.WriteAllText(path, JsonSerializer.Serialize(modules.Reverse()));
        store.LoadCatalog(); Assert.Equal(first, store.Summary);
    }

    [Fact]
    public async Task DiscoversManagedModuleAndMakesItAvailableToScripts()
    {
        using var w = new TestWorkspace();
        var store = new PowerShellModuleStore(Path.Combine(w.Root, "shared"), w.Pwsh);
        var directory = Path.Combine(store.Root, "LanAgentFixture", "1.0.0");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "LanAgentFixture.psd1"), "@{ ModuleVersion='1.0.0'; RootModule='LanAgentFixture.psm1'; FunctionsToExport=@('Get-LanAgentFixture') }");
        await File.WriteAllTextAsync(Path.Combine(directory, "LanAgentFixture.psm1"), "function Get-LanAgentFixture { param([string]$Value) 'fixture:' + $Value }; Export-ModuleMember -Function Get-LanAgentFixture");
        var catalog = await store.ExecuteAsync("list");
        Assert.Contains("Get-LanAgentFixture", catalog);
        Assert.Contains("LanAgentFixture", store.Summary);
        var restored = new PowerShellModuleStore(Path.Combine(w.Root, "shared"), w.Pwsh);
        restored.LoadCatalog();
        Assert.Equal(store.Summary, restored.Summary);
        await using var runtime = await LanAgent.Core.Runtime.AgentRuntime.CreateAsync(w.Options with { SharedDirectory = Path.Combine(w.Root, "shared") });
        Assert.Contains("LanAgentFixture", runtime.CodeAct.Instructions);
        Assert.Contains("powershell_capabilities", runtime.CodeAct.Instructions);
        var tools = new PowerShellModuleTools(store);
        var capability = await tools.CapabilitiesAsync("LanAgentFixture");
        Assert.Contains("Get-LanAgentFixture", capability.ToJson());
        var script = Path.Combine(w.Root, "use-module.ps1");
        await File.WriteAllTextAsync(script, "Get-LanAgentFixture -Value $args[0]");
        var shell = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), powerShellModuleDirectory: store.Root);
        var result = await shell.RunShellAsync(script, ["中文 $literal"]);
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("fixture:中文 $literal", JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Pester;whoami")]
    [InlineData("*")]
    [InlineData("")]
    public async Task RejectsAmbiguousInstallNamesBeforeDownload(string name)
    {
        using var w = new TestWorkspace();
        var store = new PowerShellModuleStore(w.Root, w.Pwsh);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExecuteAsync("install", name));
        Assert.False(Directory.Exists(store.Root));
    }
}
