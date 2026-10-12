using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Runtime;

public sealed record RuntimeEnvironment(string? Python, string? PowerShell, IReadOnlyList<string> Problems)
{
    public bool Ready => Python != null && PowerShell != null;
}

public sealed class EnvironmentDoctor
{
    public async Task<RuntimeEnvironment> CheckAsync(string? python = null, string? pwsh = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        string? pythonPath = null;
        var candidates = (python != null ? new[] { python } : new[] { FindExecutable("python.exe"), FindExecutable("python3.exe"), FindExecutable("py.exe") }).Where(x => x != null).Distinct();
        foreach (var candidate in candidates)
        {
            try
            {
                var args = new List<string>();
                if (string.Equals(Path.GetFileName(candidate), "py.exe", StringComparison.OrdinalIgnoreCase)) args.Add("-3");
                args.AddRange(["-I", "-S", "-c", "import sys,json; print(json.dumps({'exe':sys.executable,'major':sys.version_info.major,'minor':sys.version_info.minor}))"]);
                var info = BoundedProcess.StartInfo(candidate!, args);
                info.Environment["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false";
                var output = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(10), ct);
                using var json = JsonDocument.Parse(output.Stdout);
                if (output.ExitCode == 0 && json.RootElement.GetProperty("major").GetInt32() == 3 && json.RootElement.GetProperty("minor").GetInt32() is >= 10 and <= 14)
                {
                    pythonPath = json.RootElement.GetProperty("exe").GetString();
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (pythonPath == null) problems.Add("需要可执行的 Python 3.10–3.14（不会把 Microsoft Store 别名当作已安装）。");
        var powershell = pwsh ?? FindExecutable("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        string? powershellPath = null;
        try
        {
            var output = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]), TimeSpan.FromSeconds(10), ct);
            if (output.ExitCode == 0 && Version.TryParse(output.Stdout.Trim(), out var version) && version.Major == 7 && version.Minor == 6)
                powershellPath = Path.GetFullPath(powershell);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (powershellPath == null) problems.Add("需要 PowerShell 7.6.x；禁止退回 Windows PowerShell 5.1 或 cmd。");
        return new(pythonPath, powershellPath, problems);
    }

    public async Task<RuntimeEnvironment> EnsureAsync(Func<string, CancellationToken, ValueTask<bool>> confirmInstall,
        string? python = null, string? pwsh = null, CancellationToken ct = default)
    {
        var environment = await CheckAsync(python, pwsh, ct);
        if (environment.Ready) return environment;
        var winget = FindExecutable("winget.exe") ?? throw new InvalidOperationException("未找到 winget。请安装 App Installer 后重试。");
        foreach (var package in new[] { environment.Python == null ? "Python.Python.3.13" : null, environment.PowerShell == null ? "Microsoft.PowerShell" : null }.Where(p => p != null))
        {
            if (!await confirmInstall($"通过 winget 安装 {package}{(package == "Microsoft.PowerShell" ? " 7.6.x" : "")}？", ct)) throw new InvalidOperationException("安装未获同意，Agent 保持停用。");
            var installArgs = new List<string> { "install", "--id", package!, "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--accept-package-agreements" };
            if (package == "Microsoft.PowerShell")
            {
                var available = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(winget, ["show", "--id", package, "--exact", "--source", "winget", "--versions", "--accept-source-agreements"]), TimeSpan.FromSeconds(60), ct, 16000);
                var version = Regex.Matches(available.Stdout, @"(?m)^\s*(7\.6\.\d+)\s*$").Select(m => Version.Parse(m.Groups[1].Value)).OrderByDescending(v => v).FirstOrDefault();
                if (available.ExitCode != 0 || version == null) throw new InvalidOperationException("winget 未提供 PowerShell 7.6.x。请手动安装指定版本；Agent 保持停用。");
                installArgs.AddRange(["--version", version.ToString()]);
            }
            var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(winget, installArgs), TimeSpan.FromMinutes(10), ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"winget 安装失败：{ToolResult.Clip(result.Stderr + result.Stdout)}");
        }
        // winget may update the registry PATH without changing this process's environment.
        var refreshedPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) + ";" + Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("PATH", refreshedPath);
        environment = await CheckAsync(python, pwsh, ct);
        if (!environment.Ready) throw new InvalidOperationException(string.Join("\n", environment.Problems) + " 安装后校验未通过，请重启终端。");
        return environment;
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
}
