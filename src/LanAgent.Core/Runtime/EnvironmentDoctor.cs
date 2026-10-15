using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Runtime;

public sealed record RuntimeEnvironment(string? Python, string? PowerShell, IReadOnlyList<string> Problems)
{
    public bool Ready => Python != null && PowerShell != null;
}

public sealed class EnvironmentRestartRequiredException(string message) : InvalidOperationException(message);

public sealed class EnvironmentDoctor
{
    private readonly Func<string, string?> _find;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessOutput>> _run;

    public EnvironmentDoctor() : this(FindExecutable, (info, timeout, ct) => BoundedProcess.RunAsync(info, timeout, ct)) { }
    internal EnvironmentDoctor(Func<string, string?> find, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessOutput>> run)
    { _find = find; _run = run; }

    public async Task<RuntimeEnvironment> CheckAsync(string? python = null, string? pwsh = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        string? pythonPath = null;
        var candidates = (python != null ? new[] { python } : new[] { _find("python.exe"), _find("python3.exe"), _find("py.exe") }).Where(path => path != null).Distinct();
        foreach (var candidate in candidates)
        {
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
                {
                    pythonPath = json.RootElement.GetProperty("exe").GetString();
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (pythonPath == null) problems.Add("共享 pip 库需要可执行的 Python 3.10–3.14；缺少或版本不符时将通过 winget 安装 Python 3.13。");
        var powershell = pwsh ?? _find("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        string? powershellPath = null;
        try
        {
            var output = await _run(BoundedProcess.StartInfo(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]), TimeSpan.FromSeconds(10), ct);
            if (output.ExitCode == 0 && Version.TryParse(output.Stdout.Trim(), out var version) && version.Major == 7 && version.Minor == 6)
                powershellPath = Path.GetFullPath(powershell);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (powershellPath == null) problems.Add("未安装可用的 PowerShell 7.6。将通过 winget 安装 PowerShell 7.6，安装完成后请重启 LanAgent。");
        return new(pythonPath, powershellPath, problems);
    }

    public async Task<RuntimeEnvironment> EnsureAsync(Func<string, CancellationToken, ValueTask<bool>> confirmInstall,
        string? python = null, string? pwsh = null, CancellationToken ct = default)
    {
        var environment = await CheckAsync(python, pwsh, ct);
        if (environment.Ready) return environment;
        var installed = new List<string>();
        foreach (var package in new[] { environment.Python == null ? "Python.Python.3.13" : null, environment.PowerShell == null ? "Microsoft.PowerShell" : null }.Where(package => package != null))
        {
            var name = package == "Microsoft.PowerShell" ? "PowerShell 7.6" : "Python 3.13";
            if (!await confirmInstall($"将安装 {name}，并通过 winget 安装，安装完成之后请关闭并重新启动 LanAgent。", ct))
                throw new InvalidOperationException("安装未获同意，Agent 保持停用。");
            var winget = _find("winget.exe") ?? throw new InvalidOperationException("未找到 winget。请安装 App Installer 后重试。");
            var installArgs = new List<string> { "install", "--id", package!, "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--accept-package-agreements" };
            if (package == "Microsoft.PowerShell")
            {
                var available = await _run(BoundedProcess.StartInfo(winget, ["show", "--id", package, "--exact", "--source", "winget", "--versions", "--accept-source-agreements"]), TimeSpan.FromSeconds(60), ct);
                var version = Regex.Matches(available.Stdout, @"(?m)^\s*(7\.6\.\d+)\s*$").Select(m => Version.Parse(m.Groups[1].Value)).OrderByDescending(v => v).FirstOrDefault();
                if (available.ExitCode != 0 || version == null) throw new InvalidOperationException("winget 未提供 PowerShell 7.6.x。请手动安装指定版本；Agent 保持停用。");
                installArgs.AddRange(["--version", version.ToString()]);
            }
            var result = await _run(BoundedProcess.StartInfo(winget, installArgs), TimeSpan.FromMinutes(10), ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"winget 安装 {name} 失败：{ToolResult.Clip(result.Stderr + result.Stdout)}");
            installed.Add(name);
        }
        throw new EnvironmentRestartRequiredException(string.Join("、", installed) + " 已通过 winget 安装完成。请关闭并重新启动 LanAgent（终端），重启后将继续初始化。");
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
