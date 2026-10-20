using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

public sealed record RuntimeEnvironment(string? Python, string? PowerShell, IReadOnlyList<string> Problems)
{
    public bool Ready => PowerShell != null;
}

public sealed class EnvironmentRestartRequiredException(string message) : InvalidOperationException(message);

public sealed class EnvironmentDoctor
{
    private readonly Func<string, string?> _find;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessOutput>> _run;

    public EnvironmentDoctor() : this(FindExecutable, (info, timeout, ct) => BoundedProcess.RunAsync(info, timeout, ct)) { }
    internal EnvironmentDoctor(Func<string, string?> find, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessOutput>> run)
    { _find = find; _run = run; }

    /// <summary>Startup check: the bundled PowerShell SDK worker is the only required component.
    /// Python is resolved on demand by the Monty fallback sandbox.</summary>
    public Task<RuntimeEnvironment> CheckAsync(string? pwsh = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var powershell = pwsh ?? Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.PowerShell.exe");
        string? powershellPath = File.Exists(powershell) ? Path.GetFullPath(powershell) : null;
        if (powershellPath == null) problems.Add("随包 PowerShell SDK worker 缺失，请修复或重新安装 AllAgent；无需安装系统 PowerShell。");
        return Task.FromResult(new RuntimeEnvironment(null, powershellPath, problems));
    }

    /// <summary>Find a supported host Python for the Monty fallback sandbox. Never installs.</summary>
    public async Task<string?> ResolvePythonAsync(string? python = null, CancellationToken ct = default)
    {
        var candidates = (python != null ? new[] { python } : new[] { _find("python.exe"), _find("python3.exe"), _find("py.exe") }).Where(path => path != null).Distinct();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var args = new List<string>();
                if (string.Equals(Path.GetFileName(candidate), "py.exe", StringComparison.OrdinalIgnoreCase)) args.Add("-3");
                args.AddRange(["-I", "-S", "-c", "import sys,json; print(json.dumps({'exe':sys.executable,'major':sys.version_info.major,'minor':sys.version_info.minor}))"]);
                var info = BoundedProcess.StartInfo(candidate!, args);
                info.Environment["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false";
                var output = await _run(info, TimeSpan.FromSeconds(10), ct);
                using var json = JsonDocument.Parse(output.Stdout);
                if (output.ExitCode == 0 && json.RootElement.GetProperty("major").GetInt32() == 3 && json.RootElement.GetProperty("minor").GetInt32() is >= 10 and <= 14)
                    return json.RootElement.GetProperty("exe").GetString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return null;
    }

    /// <summary>Explicit repair action: install Python through winget when the operator asked for it.</summary>
    public async Task<string> EnsurePythonAsync(Func<string, CancellationToken, ValueTask<bool>> confirmInstall,
        string? python = null, CancellationToken ct = default)
    {
        if (await ResolvePythonAsync(python, ct) is { } resolved) return resolved;
        if (python != null) throw new InvalidOperationException($"指定的 Python 不可用：{python}");
        const string name = "Python 3.13";
        if (!await confirmInstall($"将安装 {name}（仅用于 Monty 备用沙箱初始化），通过 winget 安装，安装完成之后请关闭并重新启动 AllAgent。", ct))
            throw new InvalidOperationException("安装未获同意，Agent 保持停用。");
        var winget = _find("winget.exe") ?? throw new InvalidOperationException("未找到 winget。请安装 App Installer 后重试。");
        var installArgs = new List<string> { "install", "--id", "Python.Python.3.13", "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--accept-package-agreements" };
        var result = await _run(BoundedProcess.StartInfo(winget, installArgs), TimeSpan.FromMinutes(10), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"winget 安装 {name} 失败：{ToolResult.Clip(result.Stderr + result.Stdout)}");
        throw new EnvironmentRestartRequiredException($"{name} 已通过 winget 安装完成。请关闭并重新启动 AllAgent（终端），重启后将继续初始化。");
    }

    public static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (ArgumentException) { }
        }
        return null;
    }
    public async Task<string> EnsureRipgrepAsync(CancellationToken ct = default)
    {
        if (FindExecutable("rg.exe") is { } existing) return existing;
        var winget = FindExecutable("winget.exe") ?? throw new InvalidOperationException("未找到 winget，无法安装 ripgrep。");
        var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(winget,
            ["install", "--id", "BurntSushi.ripgrep.MSVC", "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--accept-package-agreements"]), TimeSpan.FromMinutes(10), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("ripgrep 安装失败：" + result.Stderr + result.Stdout);
        var refreshed = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) + ";" + Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("PATH", refreshed);
        return FindExecutable("rg.exe") ?? throw new InvalidOperationException("ripgrep 已安装但当前进程无法定位 rg.exe；重启终端后 /doctor install 重试。");
    }
}
