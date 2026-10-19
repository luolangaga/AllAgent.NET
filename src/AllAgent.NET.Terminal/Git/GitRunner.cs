using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Terminal;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    /// <summary>The captured output hit the per-call budget.</summary>
    public bool Truncated { get; init; }
    public string Text => string.Join("\n", new[] { Stderr, Stdout }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
}

public sealed record GitFileEntry(string Path, string? OriginalPath, string Index, string Worktree);

public sealed record GitStatusSnapshot(string? Branch, bool Detached, string? Upstream, int Ahead, int Behind,
    IReadOnlyList<GitFileEntry> Staged, IReadOnlyList<GitFileEntry> Unstaged, IReadOnlyList<GitFileEntry> Untracked,
    IReadOnlyList<GitFileEntry> Conflicted, string? Operation)
{
    public int Count => Staged.Count + Unstaged.Count + Untracked.Count + Conflicted.Count;
}

public sealed record GitCommit(string Hash, string Short, string Author, string AuthorEmail, string Date, string Subject,
    IReadOnlyList<string> Refs, IReadOnlyList<string> Parents);
public sealed record GitBranchInfo(string Name, bool Current, string? Upstream, string Date, string Subject);
public sealed record GitRemoteBranch(string Name, string Date, string Subject);
public sealed record GitTagInfo(string Name, string Date);
public sealed record GitBranches(string? Current, IReadOnlyList<GitBranchInfo> Local, IReadOnlyList<GitRemoteBranch> Remote, IReadOnlyList<GitTagInfo> Tags);
public sealed record GitStash(int Index, string Ref, string Message, string Date);
public sealed record GitShowFile(string Path, string Status, string? OriginalPath);
public sealed record GitIdentity(string? Name, string? Email);

/// <summary>
/// Runs git inside one workspace with literal arguments (never a shell string) and parses the
/// porcelain formats. Reads are cheap; the caller decides what needs confirmation. When the
/// workspace sits inside a repository, git runs from the repository root so every reported path
/// means the same thing.
/// </summary>
internal sealed class GitRunner(string workspace)
{
    private const int DefaultLimit = 128 * 1024;
    private const string FieldSeparator = "\u001f";

    private string? _root;
    private bool _rootResolved;
    private string? _version;
    private readonly object _gate = new();

    public string? Executable { get; private set; } = Locate();

    /// <summary>Re-resolves git.exe after an install refreshed PATH.</summary>
    public string? RefreshExecutable() => Executable = Locate();

    public static string? Locate()
    {
        if (EnvironmentDoctor.FindExecutable("git.exe") is { } path) return path;
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private System.Diagnostics.ProcessStartInfo StartInfo(IReadOnlyList<string> args, string? cwd = null)
    {
        var executable = Executable ?? throw new InvalidOperationException("未找到 git：请先安装 Git for Windows（面板中可直接安装）。");
        var full = new List<string> { "--no-pager", "-c", "core.quotepath=false", "-c", "color.ui=false" };
        full.AddRange(args);
        var info = BoundedProcess.StartInfo(executable, full, cwd ?? _root ?? workspace);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_EDITOR"] = ":";
        info.Environment["GIT_PAGER"] = "cat";
        return info;
    }

    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, int limit = DefaultLimit)
    {
        var info = StartInfo(args);
        var result = await BoundedProcess.RunAsync(info, timeout ?? TimeSpan.FromSeconds(60), ct, limit);
        return new GitResult(result.ExitCode, result.Stdout, result.Stderr) { Truncated = result.Truncated };
    }

    public async Task<GitResult> MustAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, int limit = DefaultLimit)
    {
        var result = await RunAsync(args, ct, timeout, limit);
        if (!result.Ok) throw new InvalidOperationException(Friendly(result));
        return result;
    }

    /// <summary>Streams to the operation console instead of one response; used for network commands.</summary>
    public async Task<GitResult> StreamAsync(IReadOnlyList<string> args, Action<string, bool> onOutput, CancellationToken ct, TimeSpan? timeout = null)
    {
        var info = StartInfo(args);
        var result = await BoundedProcess.RunAsync(info, timeout ?? TimeSpan.FromMinutes(15), ct, DefaultLimit, onOutput);
        return new GitResult(result.ExitCode, result.Stdout, result.Stderr) { Truncated = result.Truncated };
    }

    /// <summary>The repository root (or the workspace when it is not a repository).</summary>
    public async Task<string> CwdAsync(CancellationToken ct)
    {
        if (!_rootResolved)
        {
            var result = await BoundedProcess.RunAsync(StartInfo(["rev-parse", "--show-toplevel"], workspace), TimeSpan.FromSeconds(20), ct, 4096);
            var root = result.ExitCode == 0 ? result.Stdout.Trim() : string.Empty;
            lock (_gate) { _root = root.Length > 0 && Directory.Exists(root) ? root : null; _rootResolved = true; }
        }
        lock (_gate) return _root ?? workspace;
    }

    /// <summary>Forgets the cached repository root (after `git init` or a workspace move).</summary>
    public void ResetRepositoryCache()
    {
        lock (_gate) { _rootResolved = false; _root = null; }
    }

    public async Task<bool> IsRepositoryAsync(CancellationToken ct)
    {
        var cwd = await CwdAsync(ct);
        lock (_gate) return _root != null && Directory.Exists(cwd);
    }

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        if (_version != null) return _version;
        if (Executable is not { } executable) return _version = string.Empty;
        var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(executable, ["--version"], workspace), TimeSpan.FromSeconds(20), ct, 4096);
        return _version = result.ExitCode == 0 ? result.Stdout.Trim() : string.Empty;
    }

    public async Task<GitStatusSnapshot> StatusAsync(CancellationToken ct)
    {
        var result = await MustAsync(["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], ct, limit: 4 * 1024 * 1024);
        var snapshot = ParseStatus(result.Stdout);
        return snapshot with { Operation = await OperationAsync(ct) };
    }

    public static GitStatusSnapshot ParseStatus(string output)
    {
        string? branch = null;
        var detached = false;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var staged = new List<GitFileEntry>();
        var unstaged = new List<GitFileEntry>();
        var untracked = new List<GitFileEntry>();
        var conflicted = new List<GitFileEntry>();
        var tokens = output.Split('\0');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length == 0) continue;
            if (token[0] == '#')
            {
                if (token.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    var head = token[14..].Trim();
                    if (head == "(detached)") detached = true; else branch = head;
                }
                else if (token.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = token[18..].Trim();
                else if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    foreach (var part in token[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.StartsWith('+') && int.TryParse(part[1..], out var aheadValue)) ahead = aheadValue;
                        else if (part.StartsWith('-') && int.TryParse(part[1..], out var behindValue)) behind = behindValue;
                    }
                }
                continue;
            }
            switch (token[0])
            {
                case '1':
                {
                    var parts = token.Split(' ', 9);
                    if (parts.Length < 9) continue;
                    Classify(parts[1], parts[8], null, staged, unstaged);
                    break;
                }
                case '2':
                {
                    var parts = token.Split(' ', 10);
                    if (parts.Length < 10) continue;
                    var original = index + 1 < tokens.Length ? tokens[++index] : null;
                    Classify(parts[1], parts[9], original, staged, unstaged);
                    break;
                }
                case 'u':
                {
                    var parts = token.Split(' ', 11);
                    if (parts.Length < 11) continue;
                    conflicted.Add(new GitFileEntry(parts[10], null, Status(parts[1], 0), Status(parts[1], 1)));
                    break;
                }
                case '?':
                    untracked.Add(new GitFileEntry(token.Length > 2 ? token[2..] : string.Empty, null, "?", "?"));
                    break;
            }
        }
        return new GitStatusSnapshot(branch, detached, upstream, ahead, behind, staged, unstaged, untracked, conflicted, null);

        static void Classify(string xy, string path, string? original, List<GitFileEntry> staged, List<GitFileEntry> unstaged)
        {
            var index = Status(xy, 0);
            var worktree = Status(xy, 1);
            if (index != "." && index != "?") staged.Add(new GitFileEntry(path, original, index, worktree));
            if (worktree != "." && worktree != "?") unstaged.Add(new GitFileEntry(path, original, index, worktree));
        }

        static string Status(string xy, int offset) => xy.Length > offset ? xy[offset].ToString() : ".";
    }

    /// <summary>An in-progress merge/rebase/cherry-pick/revert, read from the repository markers.</summary>
    public async Task<string?> OperationAsync(CancellationToken ct)
    {
        var dir = await RunAsync(["rev-parse", "--git-dir"], ct, TimeSpan.FromSeconds(20), 4096);
        if (!dir.Ok) return null;
        string gitDir;
        try { gitDir = Path.GetFullPath(dir.Stdout.Trim(), await CwdAsync(ct)); }
        catch (ArgumentException) { return null; }
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "rebase";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        return null;
    }

    /// <summary>Paged history in topological order (children before parents, so the page can draw a
    /// commit graph). <paramref name="reference"/> narrows to one branch/tag; <paramref name="grep"/>
    /// does a fixed-string message search.</summary>
    public async Task<(IReadOnlyList<GitCommit> Commits, bool More)> LogAsync(int skip, int take, bool all, string? reference, string? grep, CancellationToken ct)
    {
        var args = new List<string>
        {
            "log", "--no-color", "--topo-order", $"--max-count={take + 1}", $"--skip={Math.Max(0, skip)}",
            $"--format=%H{FieldSeparator}%h{FieldSeparator}%an{FieldSeparator}%ae{FieldSeparator}%aI{FieldSeparator}%s{FieldSeparator}%D{FieldSeparator}%P",
        };
        if (!string.IsNullOrWhiteSpace(reference)) args.Add(reference.Trim());
        else if (all)
        {
            // Stashes are WIP snapshots, not history: drawing them as branches makes the graph noisy.
            args.Add("--exclude=refs/stash");
            args.Add("--all");
        }
        if (!string.IsNullOrWhiteSpace(grep)) { args.Add("--fixed-strings"); args.Add("--grep=" + grep.Trim()); }
        var result = await MustAsync(args, ct);
        var commits = ParseCommits(result.Stdout);
        var more = commits.Count > take;
        if (more) commits.RemoveAt(commits.Count - 1);
        return (commits, more);
    }

    public async Task<GitCommit?> CommitAsync(string hash, CancellationToken ct)
    {
        var result = await MustAsync(["log", "-1", "--no-color",
            $"--format=%H{FieldSeparator}%h{FieldSeparator}%an{FieldSeparator}%ae{FieldSeparator}%aI{FieldSeparator}%s{FieldSeparator}%D{FieldSeparator}%P", hash], ct);
        return ParseCommits(result.Stdout).FirstOrDefault();
    }

    private static List<GitCommit> ParseCommits(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\u001f');
            if (parts.Length < 8) continue;
            var parents = parts[7].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new GitCommit(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], SplitRefs(parts[6]), parents));
        }
        return commits;
    }

    public async Task<string> CommitBodyAsync(string hash, CancellationToken ct)
    {
        var result = await RunAsync(["log", "-1", "--no-color", "--format=%b", hash], ct);
        return result.Ok ? result.Stdout.Trim() : string.Empty;
    }

    public async Task<IReadOnlyList<GitShowFile>> ShowFilesAsync(string hash, CancellationToken ct)
    {
        var result = await MustAsync(["show", "--no-color", "--format=", "--name-status", "--find-renames", hash], ct, limit: 512 * 1024);
        var files = new List<GitShowFile>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0];
            if (status.StartsWith('R') && parts.Length >= 3) files.Add(new GitShowFile(parts[2], status, parts[1]));
            else files.Add(new GitShowFile(parts[1], status, null));
        }
        return files;
    }

    public async Task<GitBranches> BranchesAsync(CancellationToken ct)
    {
        var current = (await RunAsync(["branch", "--show-current"], ct, limit: 4096)).Stdout.Trim();
        var local = new List<GitBranchInfo>();
        var remote = new List<GitRemoteBranch>();
        var tags = new List<GitTagInfo>();
        var localResult = await MustAsync(["for-each-ref", $"--format=%(refname:short)@{{%(upstream:short)}}@{{%(HEAD)}}@{{%(authordate:iso-strict)}}@{{%(subject)}}", "refs/heads"], ct);
        foreach (var line in localResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(["@{"], 5, StringSplitOptions.None);
            if (fields.Length < 5) continue;
            local.Add(new GitBranchInfo(fields[0], fields[2].StartsWith('*'), Trim(fields[1]), Trim(fields[3]), fields[4].TrimEnd('}').Trim()));
        }
        var remoteResult = await MustAsync(["for-each-ref", $"--format=%(refname:short)@{{%(authordate:iso-strict)}}@{{%(subject)}}", "refs/remotes"], ct);
        foreach (var line in remoteResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(["@{"], 3, StringSplitOptions.None);
            if (fields.Length < 3) continue;
            if (fields[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            remote.Add(new GitRemoteBranch(fields[0], Trim(fields[1]), fields[2].TrimEnd('}').Trim()));
        }
        var tagResult = await MustAsync(["for-each-ref", $"--format=%(refname:short)@{{%(creatordate:iso-strict)}}", "refs/tags"], ct);
        foreach (var line in tagResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(["@{"], 2, StringSplitOptions.None);
            if (fields.Length < 2) continue;
            tags.Add(new GitTagInfo(fields[0], Trim(fields[1])));
        }
        return new GitBranches(current.Length == 0 ? null : current, local, remote, tags);

        static string Trim(string value) => value.TrimEnd('}').Trim();
    }

    public async Task<IReadOnlyList<GitStash>> StashListAsync(CancellationToken ct)
    {
        var result = await MustAsync(["stash", "list", $"--format=%gd{FieldSeparator}%gs{FieldSeparator}%cI"], ct);
        var stashes = new List<GitStash>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\u001f');
            if (parts.Length < 3) continue;
            var index = 0;
            var start = parts[0].IndexOf('{');
            var end = parts[0].IndexOf('}');
            if (start >= 0 && end > start) int.TryParse(parts[0][(start + 1)..end], out index);
            stashes.Add(new GitStash(index, parts[0], parts[1], parts[2]));
        }
        return stashes;
    }

    public async Task<GitIdentity> IdentityAsync(CancellationToken ct)
    {
        var name = await RunAsync(["config", "--get", "user.name"], ct, limit: 4096);
        var email = await RunAsync(["config", "--get", "user.email"], ct, limit: 4096);
        return new GitIdentity(name.Ok ? name.Stdout.Trim() : null, email.Ok ? email.Stdout.Trim() : null);
    }

    public async Task<ParsedPatch> DiffAsync(string? path, bool staged, string? commit, CancellationToken ct)
    {
        var args = new List<string>();
        if (commit != null)
        {
            args.AddRange(["show", "--no-color", "--format=", "--unified=3", "--find-renames", commit]);
        }
        else
        {
            args.AddRange(["diff", "--no-color", "--unified=3", "--find-renames"]);
            if (staged) args.Add("--cached");
        }
        if (!string.IsNullOrEmpty(path)) { args.Add("--"); args.Add(path); }
        var result = await MustAsync(args, ct, limit: 1024 * 1024);
        return UnifiedDiff.Parse(result.Stdout);
    }

    /// <summary>Human-readable failure: identity and repository problems get an actionable message.</summary>
    public static string Friendly(GitResult result)
    {
        var text = result.Text;
        if (text.Contains("Please tell me who you are", StringComparison.OrdinalIgnoreCase) || text.Contains("unable to auto-detect email", StringComparison.OrdinalIgnoreCase))
            return "提交身份未设置：请填写 Git 用户名与邮箱后重试。";
        if (text.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)) return "当前工作区不是 Git 仓库。";
        if (text.Contains("did not match any file", StringComparison.OrdinalIgnoreCase))
            return "所选文件已不在工作区或索引中（可能已被提交、删除或重命名）。请刷新后重试。";
        if (text.Contains("no such remote", StringComparison.OrdinalIgnoreCase)) return text + "（请检查远程仓库配置。）";
        if (text.Length == 0) return "git 命令失败（退出码 " + result.ExitCode + "）。";
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    private static IReadOnlyList<string> SplitRefs(string refs)
        => refs.Length == 0
            ? []
            : refs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
}
