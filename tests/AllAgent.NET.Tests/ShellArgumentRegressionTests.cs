using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ShellArgumentRegressionTests
{
    [Fact]
    public async Task BundledModulesWorkWithoutSystemModuleSearchPath()
    {
        using var w = new TestWorkspace();
        var path = Path.Combine(w.Root, "modules.ps1");
        await File.WriteAllTextAsync(path, """
            $ErrorActionPreference = 'Stop'
            $commands = @('Get-ChildItem', 'Get-Content', 'ConvertTo-Json', 'Invoke-WebRequest', 'Get-FileHash', 'Get-ExecutionPolicy', 'Get-CimInstance')
            foreach ($name in $commands) { $null = Get-Command $name -ErrorAction Stop }
            $null = Get-ChildItem -LiteralPath .
            $null = Get-FileHash -LiteralPath $PSCommandPath
            $null = Get-CimInstance Win32_OperatingSystem
            [Console]::WriteLine(($commands | ConvertTo-Json -Compress))
            """);
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exe = path, args = Array.Empty<string>() })));
        var info = BoundedProcess.StartInfo(w.Pwsh, ["--execute", payload], w.Root);
        info.Environment["PATH"] = "";
        info.Environment["PSModulePath"] = "";
        var result = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(30), default);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("Get-CimInstance", JsonSerializer.Deserialize<string[]>(result.Stdout)!);
    }

    [Fact]
    public async Task SdkRunsPowerShellScriptWithLiteralArgumentsAndExitCode()
    {
        using var w = new TestWorkspace();
        var path = Path.Combine(w.Root, "script with spaces.ps1");
        await File.WriteAllTextAsync(path, "[Console]::WriteLine(($args | ConvertTo-Json -Compress -AsArray)); [Console]::Error.WriteLine('stderr-proof'); exit 7");
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string> { ["script"] = path });
        string[] args = ["", "two words", "中文 😀", "$(throw 'injected'); & |", "-literal"];
        var result = await tools.RunShellAsync("script", args);
        Assert.False(result.Ok);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(7, data.GetProperty("exitCode").GetInt32());
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(data.GetProperty("stdout").GetString()!));
        Assert.Contains("stderr-proof", data.GetProperty("stderr").GetString());
    }

    [Fact]
    public async Task SdkProbeDoesNotNeedSystemPowerShellOnPath()
    {
        var info = BoundedProcess.StartInfo(Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.PowerShell.exe"), ["--probe"]);
        info.Environment["PATH"] = "";
        var result = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("AllAgent.NET.PowerShell.SDK", JsonDocument.Parse(result.Stdout).RootElement.GetProperty("runtime").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralArgumentsSurviveForegroundAndBackground(bool background)
    {
        using var w = new TestWorkspace();
        await using var jobs = new BackgroundTasks();
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []),
            new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe") }, jobs: jobs);
        string[] args = ["literal", "", "two words", "quoted \"value\"", "中文 😀", "C:\\directory with spaces\\", "a&b;$(whoami)|<>", "-o", "download file.bin"];
        var result = await tools.RunShellAsync("probe", args, background: background);
        Assert.True(result.Ok, result.ToJson());
        string output;
        if (background)
        {
            var id = JsonSerializer.SerializeToElement(result.Data).GetProperty("taskId").GetString()!;
            var job = await jobs.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundTaskState.Completed, job.State);
            output = job.Output.Split('\n')[0];
        }
        else output = JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString()!;
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(output));
        Assert.False(File.Exists(Path.Combine(w.Root, "download file.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurlWritesCompleteFileWithLiteralOutputPath(bool background)
    {
        using var w = new TestWorkspace();
        await using var jobs = new BackgroundTasks();
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []),
            new Dictionary<string, string> { ["curl"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe") }, jobs: jobs);
        var content = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
        var source = Path.Combine(w.Root, "source data.bin");
        await File.WriteAllBytesAsync(source, content);
        var result = await tools.RunShellAsync("curl", ["--fail", "--silent", "--show-error", new Uri(source).AbsoluteUri, "-o", "download file.bin"], background: background);
        Assert.True(result.Ok, result.ToJson());
        if (background)
        {
            var id = JsonSerializer.SerializeToElement(result.Data).GetProperty("taskId").GetString()!;
            var job = await jobs.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(job.State == BackgroundTaskState.Completed, job.Error + job.Output);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(w.Root, "download file.bin")));
    }
}
