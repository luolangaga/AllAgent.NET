using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AllAgent.NET.GitHub;

/// <summary>Structured git arguments only. Credentials live in the child environment, never URLs/config files.</summary>
public static partial class GitHubGit
{
    [GeneratedRegex(@"^(?:https://github\.com/|git@github\.com:)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RemotePattern();
    public static string? RemoteRepository(string remote)
    {
        var match = RemotePattern().Match(remote.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    public static async Task ValidateAsync(string directory, string repository, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new ArgumentException("请选择存在的工作区绝对路径。");
        var top = await RunAsync(directory, null, ct, "rev-parse", "--show-toplevel");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top.Trim())), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择仓库根目录作为工作区。");
        var remote = await RunAsync(directory, null, ct, "remote", "get-url", "origin");
        if (!string.Equals(RemoteRepository(remote), repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选工作区的 origin 与 GitHub 仓库不匹配。请选对应仓库，或克隆到空目录。");
    }

    public static async Task CloneAsync(string directory, string repository, string token, JsonPrTarget? pull, CancellationToken ct)
    {
        repository = GitHubClient.Repository(repository);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("克隆目录必须是绝对路径。");
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("克隆目标必须为空目录或尚不存在的目录；不会覆盖已有文件。");
        Directory.CreateDirectory(directory);
        await RunAsync(directory, token, ct, "clone", "--no-recurse-submodules", "--no-checkout", "--", $"https://github.com/{repository}.git", directory);
        if (pull != null)
        {
            await RunAsync(directory, token, ct, "fetch", "--no-tags", "--no-recurse-submodules", "origin", $"refs/pull/{pull.Number}/head");
            var fetched = (await RunAsync(directory, null, ct, "rev-parse", "FETCH_HEAD")).Trim();
            if (!string.Equals(fetched, pull.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PR 在克隆期间更新了。已保留仓库，请刷新 PR 后重试。");
            await RunAsync(directory, null, ct, "checkout", "--detach", pull.Sha);
        }
        else await RunAsync(directory, null, ct, "checkout", "--force", "HEAD");
    }

    public static async Task<string> RunAsync(string directory, string? token, CancellationToken ct, params string[] args)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", "core.hooksPath=", "-c", "http.followRedirects=false", "-c", "credential.helper=", "-c", "filter.lfs.smudge=", "-c", "filter.lfs.required=false" }.Concat(args)) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        if (token != null)
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            start.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Git。请先安装 Git for Windows。");
        using var registration = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = DrainAsync(process.StandardOutput, deadline.Token);
        var errors = DrainAsync(process.StandardError, deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        var text = await output; await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git {args.FirstOrDefault()} 失败（退出码 {process.ExitCode}）。请检查 Git、网络、仓库权限和工作区状态。克隆中断的文件会保留，请检查后选择新的空目录。");
        return text;
    }
    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
            if (result.Length < 32_000) result.Append(buffer, 0, Math.Min(count, 32_000 - result.Length));
        return result.ToString();
    }
}
public sealed record JsonPrTarget(int Number, string Sha);
