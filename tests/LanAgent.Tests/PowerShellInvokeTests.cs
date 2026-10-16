using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class PowerShellInvokeTests
{
    [Fact]
    public async Task BindsTypedParametersReturnsObjectsAndRequiresApproval()
    {
        using var w = new TestWorkspace();
        var store = new PowerShellModuleStore(w.Root, w.Pwsh);
        var moduleDirectory = Path.Combine(store.Root, "InvokeFixture", "1.0.0"); Directory.CreateDirectory(moduleDirectory);
        await File.WriteAllTextAsync(Path.Combine(moduleDirectory, "InvokeFixture.psd1"), "@{ ModuleVersion='1.0.0'; RootModule='InvokeFixture.psm1'; FunctionsToExport=@('Get-Fixture','Wait-Fixture') }");
        await File.WriteAllTextAsync(Path.Combine(moduleDirectory, "InvokeFixture.psm1"), "function Get-Fixture { param([string]$Text,[int[]]$Numbers,[switch]$Enabled) [pscustomobject]@{Text=$Text;Numbers=$Numbers;Enabled=[bool]$Enabled;Directory=$PWD.Path} }; function Wait-Fixture { Start-Sleep -Seconds 60 }; Export-ModuleMember -Function Get-Fixture,Wait-Fixture");
        await store.ExecuteAsync("list");
        var tools = new PowerShellInvokeTools(new WorkspacePolicy(w.Root), new(w.Python, w.Pwsh, []), store);
        var registry = new ToolRegistry(); registry.Register(tools);
        var parameters = JsonSerializer.SerializeToElement(new { Text = "中文 $(throw 'injection'); & |", Numbers = new[] { 1, 2 }, Enabled = true });
        var args = JsonSerializer.SerializeToElement(new { module = "InvokeFixture", command = "Get-Fixture", parameters });
        var denied = await registry.CallAsync("powershell_invoke", args, default);
        Assert.False(denied.Ok); Assert.Contains("approval denied", denied.Error, StringComparison.OrdinalIgnoreCase);
        var approved = new ToolRegistry((_, _) => ValueTask.FromResult(true)); approved.Register(tools);
        var result = await approved.CallAsync("powershell_invoke", args, default);
        Assert.True(result.Ok, result.ToJson());
        var record = JsonSerializer.SerializeToElement(result.Data).GetProperty("records")[0];
        Assert.Equal(parameters.GetProperty("Text").GetString(), record.GetProperty("Text").GetString());
        Assert.True(record.GetProperty("Enabled").GetBoolean());
        Assert.Equal(2, record.GetProperty("Numbers").GetArrayLength());
        Assert.Equal(w.Root, record.GetProperty("Directory").GetString(), ignoreCase: true);
        var metadata = await tools.CommandInfoAsync("InvokeFixture", "Get-Fixture");
        Assert.True(metadata.Ok, metadata.ToJson()); Assert.Contains("System.Int32[]", metadata.ToJson());
        var failure = await tools.InvokeCommandAsync("InvokeFixture", "Get-Fixture", JsonSerializer.SerializeToElement(new { UnknownParameter = 1 }));
        Assert.False(failure.Ok);
        var timeout = await tools.InvokeCommandAsync("InvokeFixture", "Wait-Fixture", JsonSerializer.SerializeToElement(new { }), timeout_seconds: 1);
        Assert.False(timeout.Ok); Assert.True(JsonSerializer.SerializeToElement(timeout.Data).GetProperty("timedOut").GetBoolean());
        Assert.NotNull(tools.ValidateInvocation("powershell_invoke", JsonSerializer.SerializeToElement(new { module = "InvokeFixture", command = "Get-Fixture;whoami" })));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.InvokeCommandAsync("InvokeFixture", "Wait-Fixture", JsonSerializer.SerializeToElement(new { }), ct: cancellation.Token));
        var outside = JsonSerializer.SerializeToElement(new { module = "InvokeFixture", command = "Get-Fixture", parameters = new { }, working_directory = Path.GetTempPath() });
        var external = await registry.CallAsync("powershell_invoke", outside, default);
        Assert.False(external.Ok);
        Assert.Contains("外部目录", external.Error);
    }
}
