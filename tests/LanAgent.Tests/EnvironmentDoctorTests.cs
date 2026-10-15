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

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task ChecksHostPythonVersionAndPowerShell(int minor, bool ready)
    {
        var calls = new List<string>();
        var doctor = new EnvironmentDoctor(name => name == "python.exe" ? Executable(name) : null,
            (info, _, _) => { calls.Add(info.FileName); return Task.FromResult(info.FileName.EndsWith("python.exe", StringComparison.Ordinal) ? Python(minor) : new ProcessOutput(0, "7.6.2", "", false)); });
        var environment = await doctor.CheckAsync(pwsh: Executable("pwsh.exe"));
        Assert.Equal(ready, environment.Ready);
        Assert.Equal(new[] { Executable("python.exe"), Executable("pwsh.exe") }, calls);
        if (ready) Assert.Empty(environment.Problems);
        else { Assert.Null(environment.Python); Assert.Contains(environment.Problems, problem => problem.Contains("Python")); }
    }

    [Fact]
    public async Task InstallsPinnedPowerShell76ThenRequiresRestartWithoutContinuingChecks()
    {
        var calls = new List<ProcessStartInfo>(); string? notice = null;
        var doctor = new EnvironmentDoctor(Executable, (info, _, _) =>
        {
            calls.Add(info);
            if (info.FileName.EndsWith("python.exe", StringComparison.Ordinal)) return Task.FromResult(Python());
            return Task.FromResult(info.ArgumentList[0] switch
            {
                "show" => new ProcessOutput(0, "7.7.0\n7.6.2\n7.6.12\n", "", false),
                "install" => new ProcessOutput(0, "installed", "", false),
                _ => new ProcessOutput(1, "", "missing", false)
            });
        });
        var restart = await Assert.ThrowsAsync<EnvironmentRestartRequiredException>(() => doctor.EnsureAsync((message, _) =>
        { notice = message; return ValueTask.FromResult(true); }));
        Assert.Contains("PowerShell 7.6", notice); Assert.Contains("winget", notice); Assert.Contains("重新启动", notice);
        Assert.Contains("安装完成", restart.Message); Assert.Contains("重新启动 LanAgent", restart.Message);
        Assert.Equal(4, calls.Count);
        Assert.Equal(Executable("winget.exe"), calls[3].FileName);
        Assert.Equal("Microsoft.PowerShell", calls[3].ArgumentList[2]);
        Assert.Equal("7.6.12", calls[3].ArgumentList[^1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DeniedOrFailedInstallDoesNotClaimSuccess(bool approve, bool failedInstall)
    {
        var calls = new List<ProcessStartInfo>();
        var doctor = new EnvironmentDoctor(Executable, (info, _, _) =>
        {
            calls.Add(info);
            if (info.FileName.EndsWith("python.exe", StringComparison.Ordinal)) return Task.FromResult(Python());
            return Task.FromResult(info.ArgumentList[0] == "show" ? new ProcessOutput(0, "7.6.2\n", "", false)
                : new ProcessOutput(1, "", "installation failed", false));
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => doctor.EnsureAsync((_, _) => ValueTask.FromResult(approve)));
        Assert.IsNotType<EnvironmentRestartRequiredException>(failure);
        Assert.Contains(failedInstall ? "安装 PowerShell 7.6 失败" : "安装未获同意", failure.Message);
        Assert.Equal(approve ? 4 : 2, calls.Count);
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
            return Task.FromResult(new ProcessOutput(0, info.ArgumentList[0] == "install" ? "installed" : "7.6.2", "", false));
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
