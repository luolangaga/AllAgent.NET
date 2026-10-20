using System.Diagnostics;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.CodeGraph;

/// <summary>Resolves the codegraph CLI without ever building a shell command string.</summary>
internal sealed class CodeGraphLocator(Func<string?> configuredPath, Func<bool> telemetryEnabled)
{
    private static readonly string[] ExecutableNames = ["codegraph.exe", "codegraph.cmd", "codegraph.bat", "codegraph"];

    public string? Resolve()
    {
        var configured = FromValue(configuredPath());
        if (configured != null) return configured;

        var environment = FromValue(Environment.GetEnvironmentVariable("ALLAGENT_CODEGRAPH_PATH"));
        if (environment != null) return environment;

        foreach (var name in ExecutableNames)
            if (EnvironmentDoctor.FindExecutable(name) is { } found) return Path.GetFullPath(found);

        foreach (var candidate in CommonPaths())
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    public ProcessStartInfo StartInfo(IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var executable = Resolve() ?? throw new InvalidOperationException("未找到 codegraph。请先安装 CodeGraph，或在设置中指定可执行文件路径。");
        var args = arguments.ToArray();
        var info = IsCommandScript(executable)
            ? BoundedProcess.StartInfo(ComSpec, ["/d", "/s", "/c", "call", executable, .. args], workingDirectory)
            : BoundedProcess.StartInfo(executable, args, workingDirectory);
        info.Environment["NO_COLOR"] = "1";
        if (!telemetryEnabled()) info.Environment["CODEGRAPH_TELEMETRY"] = "0";
        return info;
    }

    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        var output = await BoundedProcess.RunAsync(StartInfo(["version"]), TimeSpan.FromSeconds(15), ct);
        return output.ExitCode == 0 && !string.IsNullOrWhiteSpace(output.Stdout) ? output.Stdout.Trim() : null;
    }

    private static string? FromValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("<REDACTED>", StringComparison.Ordinal)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        if (!Directory.Exists(expanded)) return null;
        foreach (var name in ExecutableNames)
        {
            var candidate = Path.Combine(expanded, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string> CommonPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(appData, "npm", "codegraph.cmd");
        yield return Path.Combine(profile, ".codegraph", "bin", "codegraph.exe");
        yield return Path.Combine(local, "Programs", "codegraph", "codegraph.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "codegraph.exe");
    }

    private static bool IsCommandScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } value ? value : "cmd.exe";
}
