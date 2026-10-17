using System.Text.Json;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

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
        var directory = Path.Combine(store.Root, "AllAgentFixture", "1.0.0");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "AllAgentFixture.psd1"), "@{ ModuleVersion='1.0.0'; RootModule='AllAgentFixture.psm1'; FunctionsToExport=@('Get-AllAgentFixture') }");
        await File.WriteAllTextAsync(Path.Combine(directory, "AllAgentFixture.psm1"), "function Get-AllAgentFixture { param([string]$Value) 'fixture:' + $Value }; Export-ModuleMember -Function Get-AllAgentFixture");
        var catalog = await store.ExecuteAsync("list");
        Assert.Contains("Get-AllAgentFixture", catalog);
        Assert.Contains("AllAgentFixture", store.Summary);
        var restored = new PowerShellModuleStore(Path.Combine(w.Root, "shared"), w.Pwsh);
        restored.LoadCatalog();
        Assert.Equal(store.Summary, restored.Summary);
        await using var runtime = await AllAgent.NET.Core.Runtime.AgentRuntime.CreateAsync(w.Options with { SharedDirectory = Path.Combine(w.Root, "shared") });
        Assert.Contains("AllAgentFixture", runtime.CodeAct.Instructions);
        Assert.Contains("powershell_capabilities", runtime.CodeAct.Instructions);
        var tools = new PowerShellModuleTools(store);
        var capability = await tools.CapabilitiesAsync("AllAgentFixture");
        Assert.Contains("Get-AllAgentFixture", capability.ToJson());
        var script = Path.Combine(w.Root, "use-module.ps1");
        await File.WriteAllTextAsync(script, "Get-AllAgentFixture -Value $args[0]");
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
