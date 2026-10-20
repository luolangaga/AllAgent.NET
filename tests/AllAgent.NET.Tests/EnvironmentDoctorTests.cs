using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class EnvironmentDoctorTests
{
    private static string Fixture(string name) => Path.Combine(Path.GetTempPath(), "AllAgent.DoctorFixture", Guid.NewGuid().ToString("N"), name);

    private static ProcessOutput Python(int minor = 13) => new(0,
        JsonSerializer.Serialize(new { exe = "python.exe", major = 3, minor }), "", false);

    [Fact]
    public async Task BundledWorkerAloneMakesTheRuntimeReadyWithoutProbingPython()
    {
        var worker = Fixture("AllAgent.NET.PowerShell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
        await File.WriteAllTextAsync(worker, "");
        var calls = new List<ProcessStartInfo>();
        try
        {
            var doctor = new EnvironmentDoctor(_ => null, (info, _, _) => { calls.Add(info); return Task.FromResult(Python()); });
            var environment = await doctor.CheckAsync(worker);
            Assert.True(environment.Ready);
            Assert.Null(environment.Python);
            Assert.Empty(environment.Problems);
            Assert.Empty(calls);
        }
        finally { Directory.Delete(Path.GetDirectoryName(worker)!, true); }
    }

    [Fact]
    public async Task MissingBundledWorkerIsReportedWithoutInstallingSystemPowerShell()
    {
        var doctor = new EnvironmentDoctor(name => name == "winget.exe" ? Fixture(name) : null,
            (info, _, _) => Task.FromResult(new ProcessOutput(1, "", "missing", false)));
        var environment = await doctor.CheckAsync(Fixture("absent-pwsh.exe"));
        Assert.False(environment.Ready);
        Assert.Contains(environment.Problems, problem => problem.Contains("PowerShell SDK worker"));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task ResolvesOnlySupportedHostPython(int minor, bool supported)
    {
        var doctor = new EnvironmentDoctor(name => name == "python.exe" ? Fixture(name) : null,
            (_, _, _) => Task.FromResult(Python(minor)));
        var resolved = await doctor.ResolvePythonAsync();
        Assert.Equal(supported ? "python.exe" : null, resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedOrFailedPythonInstallDoesNotClaimSuccess(bool approve)
    {
        var doctor = new EnvironmentDoctor(name => name is "python3.exe" or "py.exe" ? null : Fixture(name), (info, _, _) =>
            Task.FromResult(info.ArgumentList[0] == "install" ? new ProcessOutput(1, "", "failed", false) : new ProcessOutput(1, "", "missing", false)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => doctor.EnsurePythonAsync((_, _) => ValueTask.FromResult(approve)));
        Assert.IsNotType<EnvironmentRestartRequiredException>(error);
        Assert.Contains(approve ? "安装 Python 3.13 失败" : "安装未获同意", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(15)]
    public async Task MissingOrUnsupportedPythonIsInstalledOnlyOnExplicitRepair(int minor)
    {
        var calls = new List<ProcessStartInfo>(); var notices = new List<string>();
        var doctor = new EnvironmentDoctor(name => name is "python3.exe" or "py.exe" ? null : Fixture(name), (info, _, _) =>
        {
            calls.Add(info);
            if (info.FileName.EndsWith("python.exe", StringComparison.Ordinal)) return Task.FromResult(minor < 0 ? new ProcessOutput(1, "", "missing", false) : Python(minor));
            return Task.FromResult(info.ArgumentList[0] == "install" ? new ProcessOutput(0, "installed", "", false) : new ProcessOutput(1, "", "missing", false));
        });
        var restart = await Assert.ThrowsAsync<EnvironmentRestartRequiredException>(() => doctor.EnsurePythonAsync((message, _) =>
        { notices.Add(message); return ValueTask.FromResult(true); }));
        Assert.Single(notices); Assert.Contains("Python 3.13", notices[0]);
        Assert.Contains("Python 3.13", restart.Message); Assert.Contains("重新启动", restart.Message);
        var install = Assert.Single(calls, info => info.ArgumentList[0] == "install");
        Assert.Equal("Python.Python.3.13", install.ArgumentList[2]);
        Assert.Equal(2, calls.Count);
    }
}
