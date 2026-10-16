using System.Diagnostics;
using System.Text.Json;
using LanAgent.Core.Runtime;
using Xunit;

namespace LanAgent.Tests;

public sealed class EnvironmentDoctorTests
{
    private static string Executable(string name) => Path.Combine(Path.GetTempPath(), "LanAgent.DoctorFixture", name);

    private static ProcessOutput Python(int minor = 13) => new(0,
        JsonSerializer.Serialize(new { exe = Executable("python.exe"), major = 3, minor }), "", false);

    private static ProcessOutput Sdk() => new(0, JsonSerializer.Serialize(new { runtime = "LanAgent.PowerShell.SDK", version = "7.6.7" }), "", false);

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task ChecksHostPythonVersionAndPowerShell(int minor, bool ready)
    {
        var calls = new List<string>();
        var doctor = new EnvironmentDoctor(name => name == "python.exe" ? Executable(name) : null,
            (info, _, _) => { calls.Add(info.FileName); return Task.FromResult(info.FileName.EndsWith("python.exe", StringComparison.Ordinal) ? Python(minor) : Sdk()); });
        var environment = await doctor.CheckAsync(pwsh: Executable("pwsh.exe"));
        Assert.Equal(ready, environment.Ready);
        Assert.Equal(new[] { Executable("python.exe"), Executable("pwsh.exe") }, calls);
        if (ready) Assert.Empty(environment.Problems);
        else { Assert.Null(environment.Python); Assert.Contains(environment.Problems, problem => problem.Contains("Python")); }
    }

    [Fact]
    public async Task MissingBundledSdkDoesNotInstallSystemPowerShell()
    {
        var calls = new List<ProcessStartInfo>();
        var doctor = new EnvironmentDoctor(Executable, (info, _, _) =>
        {
            calls.Add(info);
            return Task.FromResult(info.FileName.EndsWith("python.exe", StringComparison.Ordinal) ? Python() : new ProcessOutput(1, "", "missing", false));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => doctor.EnsureAsync((_, _) => throw new Exception("Must not request installation")));
        Assert.Contains("SDK worker", error.Message);
        Assert.Equal(2, calls.Count);
        Assert.Equal("--probe", calls[1].ArgumentList[0]);
        Assert.DoesNotContain(calls, call => call.FileName.EndsWith("winget.exe"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedOrFailedPythonInstallDoesNotClaimSuccess(bool approve)
    {
        var doctor = new EnvironmentDoctor(name => name is "python3.exe" or "py.exe" ? null : Executable(name), (info, _, _) =>
            Task.FromResult(info.ArgumentList[0] == "--probe" ? Sdk() : new ProcessOutput(1, "", "missing", false)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => doctor.EnsureAsync((_, _) => ValueTask.FromResult(approve)));
        Assert.IsNotType<EnvironmentRestartRequiredException>(error);
        Assert.Contains(approve ? "安装 Python 3.13 失败" : "安装未获同意", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(15)]
    public async Task MissingOrUnsupportedPythonIsInstalledForSharedLibraries(int minor)
    {
        var calls = new List<ProcessStartInfo>(); var notices = new List<string>();
        var doctor = new EnvironmentDoctor(name => name == "python3.exe" || name == "py.exe" ? null : Executable(name), (info, _, _) =>
        {
            calls.Add(info);
            if (info.FileName.EndsWith("python.exe", StringComparison.Ordinal)) return Task.FromResult(minor < 0 ? new ProcessOutput(1, "", "missing", false) : Python(minor));
            return Task.FromResult(info.ArgumentList[0] == "install" ? new ProcessOutput(0, "installed", "", false) : Sdk());
        });
        var restart = await Assert.ThrowsAsync<EnvironmentRestartRequiredException>(() => doctor.EnsureAsync((message, _) =>
        { notices.Add(message); return ValueTask.FromResult(true); }));
        Assert.Single(notices); Assert.Contains("Python 3.13", notices[0]);
        Assert.Contains("Python 3.13", restart.Message); Assert.Contains("重新启动", restart.Message);
        var install = Assert.Single(calls, info => info.ArgumentList[0] == "install");
        Assert.Equal("Python.Python.3.13", install.ArgumentList[2]);
        Assert.Equal(3, calls.Count);
    }
}
