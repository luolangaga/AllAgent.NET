using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PowerShellScriptTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public PowerShellSessions Sessions { get; }
        public PowerShellScriptTools Tools { get; }
        public Fixture()
        {
            Sessions = new PowerShellSessions(Workspace.Pwsh, new PowerShellModuleStore(Workspace.Root, Workspace.Pwsh).Root, Workspace.Root);
            Tools = new PowerShellScriptTools(new WorkspacePolicy(Workspace.Root), new(Workspace.Python, Workspace.Pwsh, []), Sessions);
        }
        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            Workspace.Dispose();
        }
    }

    [Fact]
    public async Task PersistentSessionKeepsVariablesAcrossCalls()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Tools.RunScriptAsync("$count = 41");
        Assert.True(first.Ok, first.ToJson());
        var second = await fixture.Tools.RunScriptAsync("$count + 1");
        Assert.True(second.Ok, second.ToJson());
        Assert.Equal(42, JsonSerializer.SerializeToElement(second.Data).GetProperty("records")[0].GetInt32());
    }

    [Fact]
    public async Task ObjectsModeReturnsProjectionsAndStreams()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Tools.RunScriptAsync("[pscustomobject]@{ Name='中文'; Count=2 }; Write-Warning 'careful'; Write-Verbose 'detail' -Verbose; Write-Debug 'dbg' -Debug; Write-Progress -Activity 'act' -Status 'st' -PercentComplete 50; Write-Error 'boom' -ErrorAction Continue");
        Assert.False(result.Ok);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var record = data.GetProperty("records")[0];
        Assert.Equal("中文", record.GetProperty("Name").GetString());
        Assert.Equal(2, record.GetProperty("Count").GetInt32());
        var streams = data.GetProperty("streams");
        Assert.Contains("careful", streams.GetProperty("warnings")[0].GetString()!);
        Assert.Contains("detail", streams.GetProperty("verbose")[0].GetString()!);
        Assert.Contains("dbg", streams.GetProperty("debug")[0].GetString()!);
        Assert.Contains("act", streams.GetProperty("progress")[0].GetString()!);
        Assert.Contains("boom", streams.GetProperty("errors")[0].GetString()!);
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.Equal("main", data.GetProperty("session").GetString());
    }

    [Fact]
    public async Task SchemaModeDescribesResultTypesAndMembers()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Tools.RunScriptAsync("Get-Item .", output: "schema");
        Assert.True(result.Ok, result.ToJson());
        var type = JsonSerializer.SerializeToElement(result.Data).GetProperty("types")[0];
        Assert.Contains("DirectoryInfo", type.GetProperty("typeNames")[0].GetString());
        Assert.Equal(1, type.GetProperty("count").GetInt32());
        var properties = type.GetProperty("properties").EnumerateArray().Select(property => property.GetProperty("name").GetString()).ToArray();
        Assert.Contains("FullName", properties);
        Assert.NotEmpty(type.GetProperty("methods").EnumerateArray());
    }

    [Fact]
    public async Task TextModeAndResetClearSessionState()
    {
        await using var fixture = new Fixture();
        var text = await fixture.Tools.RunScriptAsync("$marker = 'kept'; Write-Output 'first'", output: "text");
        Assert.True(text.Ok, text.ToJson());
        Assert.Contains("first", JsonSerializer.SerializeToElement(text.Data).GetProperty("text").GetString());
        var present = await fixture.Tools.RunScriptAsync("$null -ne (Get-Variable marker -ErrorAction Ignore)");
        Assert.True(JsonSerializer.SerializeToElement(present.Data).GetProperty("records")[0].GetBoolean());
        var reset = await fixture.Tools.RunScriptAsync("$null -ne (Get-Variable marker -ErrorAction Ignore)", reset: true);
        Assert.True(reset.Ok, reset.ToJson());
        Assert.False(JsonSerializer.SerializeToElement(reset.Data).GetProperty("records")[0].GetBoolean());
    }

    [Fact]
    public async Task TimeoutKillsSessionAndReportsRestart()
    {
        await using var fixture = new Fixture();
        var warm = await fixture.Tools.RunScriptAsync("$keep = 1");
        Assert.True(warm.Ok, warm.ToJson());
        var timedOut = await fixture.Tools.RunScriptAsync("Start-Sleep -Seconds 60", timeout_seconds: 1);
        Assert.False(timedOut.Ok);
        Assert.True(JsonSerializer.SerializeToElement(timedOut.Data).GetProperty("timedOut").GetBoolean());
        var restart = await fixture.Tools.RunScriptAsync("$null -ne (Get-Variable keep -ErrorAction Ignore)");
        Assert.True(restart.Ok, restart.ToJson());
        var data = JsonSerializer.SerializeToElement(restart.Data);
        Assert.True(data.GetProperty("sessionRestarted").GetBoolean());
        Assert.False(data.GetProperty("records")[0].GetBoolean());
    }

    [Fact]
    public async Task ApprovalDeniedBlocksExecution()
    {
        await using var fixture = new Fixture();
        var registry = new ToolRegistry();
        registry.Register(fixture.Tools);
        var denied = await registry.CallAsync("powershell_script", JsonSerializer.SerializeToElement(new { script = "1" }), default);
        Assert.False(denied.Ok);
        Assert.Contains("approval denied", denied.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalDirectoryIsRejected()
    {
        await using var fixture = new Fixture();
        var registry = new ToolRegistry();
        registry.Register(fixture.Tools);
        var external = await registry.CallAsync("powershell_script", JsonSerializer.SerializeToElement(new { script = "1", working_directory = Path.GetTempPath() }), default);
        Assert.False(external.Ok);
        Assert.Contains("外部目录", external.Error);
    }

    [Fact]
    public async Task WindowsManagementProvidersReturnObjects()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var cim = await fixture.Tools.RunScriptAsync("Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version");
        Assert.True(cim.Ok, cim.ToJson());
        Assert.False(string.IsNullOrEmpty(JsonSerializer.SerializeToElement(cim.Data).GetProperty("records")[0].GetProperty("Caption").GetString()));
        var discovery = await fixture.Tools.RunScriptAsync("(Get-Command -Noun *Service* | Select-Object -First 3).Count");
        Assert.True(discovery.Ok, discovery.ToJson());
        Assert.True(JsonSerializer.SerializeToElement(discovery.Data).GetProperty("records")[0].GetInt32() > 0);
        var registry = await fixture.Tools.RunScriptAsync("(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion' -Name ProductName).ProductName", output: "text");
        Assert.True(registry.Ok, registry.ToJson());
        Assert.Contains("Windows", JsonSerializer.SerializeToElement(registry.Data).GetProperty("text").GetString());
    }

    [Fact]
    public async Task NativeCommandOutputAndExitCodeAreCaptured()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var result = await fixture.Tools.RunScriptAsync("cmd /c \"echo raw-native & exit 3\"");
        Assert.False(result.Ok);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(3, data.GetProperty("exitCode").GetInt32());
        Assert.Contains("raw-native", data.GetProperty("records")[0].GetString()!);
        var console = await fixture.Tools.RunScriptAsync("[Console]::WriteLine('console-direct')");
        Assert.True(console.Ok, console.ToJson());
        Assert.Contains("console-direct", JsonSerializer.SerializeToElement(console.Data).GetProperty("consoleOutput").GetString()!);
    }

    [Fact]
    public async Task WriteHostLandsInInformationStream()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Tools.RunScriptAsync("Write-Host 'host-msg'; 'object-output'");
        Assert.True(result.Ok, result.ToJson());
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal("object-output", data.GetProperty("records")[0].GetString());
        Assert.Contains("host-msg", data.GetProperty("streams").GetProperty("information")[0].GetString()!);
    }

    [Fact]
    public async Task ServeProtocolPersistsStateAndResets()
    {
        using var workspace = new TestWorkspace();
        var info = BoundedProcess.StartInfo(workspace.Pwsh, ["--serve"], workspace.Root);
        info.RedirectStandardInput = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.Environment["ALLAGENT_PS_MODULE_ROOT"] = new PowerShellModuleStore(workspace.Root, workspace.Pwsh).Root;
        using var process = new Process { StartInfo = info };
        process.Start();
        Assert.Equal("ready", JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!).RootElement.GetProperty("kind").GetString());
        async Task<JsonElement> Run(string frame)
        {
            await process.StandardInput.WriteLineAsync(frame);
            await process.StandardInput.FlushAsync();
            return JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!).RootElement.Clone();
        }
        var first = await Run("""{"id":1,"kind":"run","script":"$p = 5; $p","output":"objects"}""");
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal(5, first.GetProperty("data").GetProperty("records")[0].GetInt32());
        var second = await Run("""{"id":2,"kind":"run","script":"$p * 2","output":"objects"}""");
        Assert.Equal(10, second.GetProperty("data").GetProperty("records")[0].GetInt32());
        var reset = await Run("""{"id":3,"kind":"run","script":"$null -ne (Get-Variable p -ErrorAction Ignore)","output":"objects","reset":true}""");
        Assert.False(reset.GetProperty("data").GetProperty("records")[0].GetBoolean());
        BoundedProcess.Kill(process);
    }
}
