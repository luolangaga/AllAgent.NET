using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Terminal;

// Payloads of the browser's Git panel. Reads go straight through; every mutation is UI-confirmed
// and runs with literal arguments, so no shell string is ever built.
public sealed record GitSummaryDto(bool Available, string Version, bool Repo, string Root, string? Branch, bool Detached,
    string? Upstream, int Ahead, int Behind, string? Operation, int Staged, int Unstaged, int Untracked, int Conflicted);
public sealed record GitStatusDto(string? Branch, bool Detached, string? Upstream, int Ahead, int Behind, string? Operation,
    IReadOnlyList<GitFileEntry> Staged, IReadOnlyList<GitFileEntry> Unstaged, IReadOnlyList<GitFileEntry> Untracked,
    IReadOnlyList<GitFileEntry> Conflicted);
public sealed record GitDiffParams(string? Path = null, bool Staged = false, string? Commit = null);
public sealed record GitDiffDto(string Path, string? Commit, bool Staged, int Added, int Removed, bool Truncated, bool Binary,
    IReadOnlyList<DiffRow> Lines, string? Note);
public sealed record GitLogParams(int Skip = 0, int Take = 50, bool All = false, string? Ref = null, string? Grep = null);
public sealed record GitLogDto(IReadOnlyList<GitCommit> Commits, bool More);
public sealed record GitShowParams(string Hash);
public sealed record GitShowDto(GitCommit Commit, string Body, IReadOnlyList<GitShowFile> Files);
public sealed record GitPathsParams(IReadOnlyList<string> Paths, bool All = false);
public sealed record GitDiscardParams(IReadOnlyList<string> Paths, bool Staged = false, bool Untracked = false);
public sealed record GitCommitParams(string Message, bool Amend = false, IReadOnlyList<string>? Paths = null);
public sealed record GitIdentityParams(string Name, string Email);
public sealed record GitSwitchParams(string Name, bool Create = false, string? StartPoint = null);
public sealed record GitBranchNameParams(string Name, bool Force = false);
public sealed record GitMergeParams(string Name);
public sealed record GitResolveParams(string Path, string Side);
public sealed record GitResetParams(string Hash);
public sealed record GitPullParams(bool Rebase = false);
public sealed record GitPushParams(bool SetUpstream = false, bool ForceWithLease = false);
public sealed record GitStashPushParams(string? Message = null, bool IncludeUntracked = false);
public sealed record GitStashIndexParams(int Index);
public sealed record GitJobDto(string Id, string Label);
public sealed record GitGenerateParams(string? ApiConfigId = null);
public sealed record GitRemoteInfo(string Name, string Url, string PushUrl);
public sealed record GitInstallResult(bool Ok, string Message);

/// <summary>
/// The `git.*` bridge group, bound to one workspace's repository. Long network commands (fetch,
/// pull, push) start a job and stream `git.output` lines plus a final `git.done`, so a browser
/// request never has to sit on the default 60-second timeout. `complete` is the injected one-shot
/// model call behind AI commit-message drafting (prompt, explicit API config id, token); null keeps
/// the panel fully manual.
/// </summary>
internal sealed class GitBridge(string workspace, BridgeEventPusher pusher, Func<string, string?, CancellationToken, Task<string>>? complete = null)
{
    private const int MaxPaths = 5000;
    private const int MaxMessage = 16_000;
    private const int MaxJobLines = 2000;
    private const int MaxUntrackedBytes = 256 * 1024;
    private const int MaxPromptCharacters = 160_000;
    private const int MaxDraftCharacters = 4000;

    private static readonly Regex BranchName = new(@"^[^\s~^:?*\[\\]+$", RegexOptions.Compiled);
    private static readonly Regex Hash = new("^[0-9a-fA-F]{4,64}$", RegexOptions.Compiled);

    private readonly GitRunner _runner = new(workspace);

    public void Register(BridgeDispatcher dispatcher)
    {
        dispatcher.Register<GitSummaryDto>("git.summary", (ct) => SummaryAsync(ct));
        dispatcher.Register<GitStatusDto>("git.status", (ct) => StatusAsync(ct));
        dispatcher.Register<GitDiffParams, GitDiffDto>("git.diff", DiffAsync);
        dispatcher.Register<GitLogParams, GitLogDto>("git.log", async (parameters, ct) =>
        {
            var take = Math.Clamp(parameters.Take <= 0 ? 50 : parameters.Take, 1, 200);
            // A ref narrows to one branch/tag; otherwise --all draws the whole graph.
            var reference = string.IsNullOrWhiteSpace(parameters.Ref) ? null : ValidateBranch(parameters.Ref);
            var grep = (parameters.Grep ?? string.Empty).Trim();
            if (grep.Length > 200) throw new ArgumentException("搜索文本过长。");
            var (commits, more) = await _runner.LogAsync(Math.Max(0, parameters.Skip), take, parameters.All, reference, grep.Length == 0 ? null : grep, ct);
            return new GitLogDto(commits, more);
        });
        dispatcher.Register<GitShowParams, GitShowDto>("git.show", async (parameters, ct) =>
        {
            ValidateHash(parameters.Hash);
            var commit = await _runner.CommitAsync(parameters.Hash, ct) ?? throw new InvalidOperationException("找不到提交：" + parameters.Hash);
            var body = await _runner.CommitBodyAsync(parameters.Hash, ct);
            var files = await _runner.ShowFilesAsync(parameters.Hash, ct);
            return new GitShowDto(commit, body, files);
        });
        dispatcher.Register<GitPathsParams, GitStatusDto>("git.stage", async (parameters, ct) =>
        {
            if (parameters.All) await _runner.MustAsync(["add", "-A"], ct, TimeSpan.FromMinutes(2));
            else await _runner.MustAsync(Args(["add", "--"], Clean(parameters.Paths)), ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitPathsParams, GitStatusDto>("git.unstage", async (parameters, ct) =>
        {
            if (parameters.All)
            {
                var all = await _runner.RunAsync(["reset", "-q"], ct, TimeSpan.FromMinutes(2));
                if (!all.Ok) await _runner.MustAsync(["rm", "--cached", "-r", "-q", "--", "."], ct, TimeSpan.FromMinutes(2));
            }
            else
            {
                var paths = Clean(parameters.Paths);
                var result = await _runner.RunAsync(Args(["reset", "-q", "--"], paths), ct, TimeSpan.FromMinutes(2));
                // The unborn-HEAD case (fresh repository, nothing committed yet) cannot reset.
                if (!result.Ok) await _runner.MustAsync(Args(["rm", "--cached", "-r", "-q", "--"], paths), ct, TimeSpan.FromMinutes(2));
            }
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitDiscardParams, GitStatusDto>("git.discard", async (parameters, ct) =>
        {
            var paths = Clean(parameters.Paths);
            if (parameters.Untracked)
            {
                await _runner.MustAsync(Args(["clean", "-f", "-d", "--"], paths), ct, TimeSpan.FromMinutes(2));
            }
            else
            {
                var args = new List<string> { "restore", "--worktree" };
                if (parameters.Staged) { args.Add("--staged"); args.Add("--source=HEAD"); }
                await _runner.MustAsync(Args(args, paths), ct, TimeSpan.FromMinutes(2));
            }
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitCommitParams, GitStatusDto>("git.commit", async (parameters, ct) =>
        {
            var message = (parameters.Message ?? string.Empty).Trim();
            if (message.Length == 0 && !parameters.Amend) throw new ArgumentException("请填写提交信息。");
            if (message.Length > MaxMessage) throw new ArgumentException("提交信息过长。");
            // The single-list UI checks the files to include: stage them, then drop index entries
            // of unchecked files so the commit carries exactly the checked selection. Paths that
            // vanished since the page rendered (already committed/deleted) are ignored.
            if (parameters.Paths is { Count: > 0 })
            {
                var requested = Clean(parameters.Paths);
                var current = await _runner.StatusAsync(ct);
                var known = new HashSet<string>(current.Staged.Concat(current.Unstaged).Concat(current.Untracked).Concat(current.Conflicted)
                    .Select(entry => entry.Path), StringComparer.Ordinal);
                var selection = requested.Where(known.Contains).ToArray();
                if (selection.Length == 0) throw new InvalidOperationException("所选文件已不在更改列表中（可能已被提交或删除），请刷新后重试。");
                // A path-limited `git add -A -- path` fails for a staged deletion (the path exists in
                // neither the index nor the worktree), so stage the whole tree and reset the rest.
                await _runner.MustAsync(["add", "-A"], ct, TimeSpan.FromMinutes(2));
                var staged = await _runner.StatusAsync(ct);
                var extras = staged.Staged.Select(entry => entry.Path).Where(path => !selection.Contains(path)).ToArray();
                if (extras.Length > 0)
                {
                    var reset = await _runner.RunAsync(Args(["reset", "-q", "--"], extras), ct, TimeSpan.FromMinutes(2));
                    if (!reset.Ok) await _runner.MustAsync(Args(["rm", "--cached", "-r", "-q", "--"], extras), ct, TimeSpan.FromMinutes(2));
                }
            }
            var args = new List<string> { "commit" };
            if (parameters.Amend) args.Add("--amend");
            if (message.Length > 0) { args.Add("-m"); args.Add(message); }
            else args.Add("--no-edit");
            await _runner.MustAsync(args, ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitIdentity>("git.identity", (ct) => _runner.IdentityAsync(ct));
        dispatcher.Register<GitIdentityParams, GitIdentity>("git.setIdentity", async (parameters, ct) =>
        {
            var name = (parameters.Name ?? string.Empty).Trim();
            var email = (parameters.Email ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 128) throw new ArgumentException("请填写 Git 用户名。");
            if (!email.Contains('@') || email.Length > 256) throw new ArgumentException("请填写有效的 Git 邮箱。");
            await _runner.MustAsync(["config", "user.name", name], ct);
            await _runner.MustAsync(["config", "user.email", email], ct);
            return await _runner.IdentityAsync(ct);
        });
        dispatcher.Register<GitBranches>("git.branches", (ct) => _runner.BranchesAsync(ct));
        dispatcher.Register<GitSwitchParams, GitStatusDto>("git.switch", async (parameters, ct) =>
        {
            var name = ValidateBranch(parameters.Name);
            var args = new List<string> { "switch" };
            if (parameters.Create)
            {
                args.Add("-c"); args.Add(name);
                if (!string.IsNullOrWhiteSpace(parameters.StartPoint)) args.Add((parameters.StartPoint ?? string.Empty).Trim());
            }
            else args.Add(name);
            await _runner.MustAsync(args, ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitBranchNameParams, GitStatusDto>("git.deleteBranch", async (parameters, ct) =>
        {
            var name = ValidateBranch(parameters.Name);
            await _runner.MustAsync(["branch", parameters.Force ? "-D" : "-d", name], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitMergeParams, GitStatusDto>("git.merge", async (parameters, ct) =>
        {
            var name = ValidateBranch(parameters.Name);
            var result = await _runner.RunAsync(["merge", "--no-edit", name], ct, TimeSpan.FromMinutes(5));
            // A conflicted merge exits non-zero but leaves the expected state; anything else is an error.
            var status = await StatusAsync(ct);
            if (!result.Ok && status.Conflicted.Count == 0 && status.Operation == null) throw new InvalidOperationException(GitRunner.Friendly(result));
            return status;
        });
        dispatcher.Register<GitMergeParams, GitStatusDto>("git.rebase", async (parameters, ct) =>
        {
            var name = ValidateBranch(parameters.Name);
            var result = await _runner.RunAsync(["rebase", name], ct, TimeSpan.FromMinutes(5));
            var status = await StatusAsync(ct);
            if (!result.Ok && status.Operation == null) throw new InvalidOperationException(GitRunner.Friendly(result));
            return status;
        });
        // Conflict resolution: take one side for a file and mark it resolved (git add).
        dispatcher.Register<GitResolveParams, GitStatusDto>("git.resolveConflict", async (parameters, ct) =>
        {
            var paths = Clean([parameters.Path ?? string.Empty]);
            var side = (parameters.Side ?? string.Empty).Trim().ToLowerInvariant();
            if (side is not ("ours" or "theirs")) throw new ArgumentException("解决方式只能是 ours 或 theirs。");
            await _runner.MustAsync(Args(["checkout", side == "ours" ? "--ours" : "--theirs", "--"], paths), ct, TimeSpan.FromMinutes(2));
            await _runner.MustAsync(Args(["add", "--"], paths), ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitResetParams, GitStatusDto>("git.resetSoft", async (parameters, ct) =>
        {
            ValidateHash(parameters.Hash);
            await _runner.MustAsync(["reset", "--soft", parameters.Hash.Trim()], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        // The SCM panel's "撤销上次提交": HEAD moves back one commit, changes stay staged.
        dispatcher.Register<GitStatusDto>("git.undoLastCommit", async (ct) =>
        {
            await _runner.MustAsync(["reset", "--soft", "HEAD~1"], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitStatusDto>("git.operationContinue", async (ct) =>
        {
            var operation = await _runner.OperationAsync(ct) ?? throw new InvalidOperationException("当前没有进行中的操作。");
            await _runner.MustAsync([operation, "--continue"], ct, TimeSpan.FromMinutes(5));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitStatusDto>("git.operationAbort", async (ct) =>
        {
            var operation = await _runner.OperationAsync(ct) ?? throw new InvalidOperationException("当前没有进行中的操作。");
            await _runner.MustAsync([operation, "--abort"], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<List<GitRemoteInfo>>("git.remotes", async (ct) =>
        {
            var result = await _runner.MustAsync(["remote", "-v"], ct);
            var remotes = new Dictionary<string, (string Url, string Push)>(StringComparer.Ordinal);
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');
                if (fields.Length < 2) continue;
                var name = fields[0];
                var url = fields[1].Split(" (")[0].Trim();
                var push = fields[1].Contains("(push)");
                var existing = remotes.TryGetValue(name, out var current) ? current : (Url: string.Empty, Push: string.Empty);
                remotes[name] = push ? (existing.Url, url) : (url, existing.Push);
            }
            return remotes.Select(pair => new GitRemoteInfo(pair.Key, pair.Value.Url, pair.Value.Push)).ToList();
        });
        dispatcher.Register<GitJobDto>("git.fetch", _ => Task.FromResult(StartJob("获取远程更新", ["fetch", "--all", "--prune", "--progress"])));
        dispatcher.Register<GitPullParams, GitJobDto>("git.pull", (parameters, _) =>
            Task.FromResult(StartJob("拉取更新", ["pull", "--progress", parameters.Rebase ? "--rebase" : "--no-rebase"])));
        dispatcher.Register<GitPushParams, GitJobDto>("git.push", async (parameters, ct) =>
        {
            var args = new List<string> { "push", "--progress" };
            if (parameters.SetUpstream)
            {
                var status = await _runner.StatusAsync(ct);
                var branch = status.Branch ?? throw new InvalidOperationException("当前处于游离 HEAD 状态，无法设置上游分支。");
                args.Add("--set-upstream"); args.Add("origin"); args.Add(branch);
            }
            if (parameters.ForceWithLease) args.Add("--force-with-lease");
            return StartJob(parameters.SetUpstream ? "推送并设置上游" : "推送", args);
        });
        dispatcher.Register<GitStatusDto>("git.init", async (ct) =>
        {
            await _runner.MustAsync(["init"], ct, TimeSpan.FromMinutes(2));
            _runner.ResetRepositoryCache();
            return await StatusAsync(ct);
        });
        dispatcher.Register<IReadOnlyList<GitStash>>("git.stashList", (ct) => _runner.StashListAsync(ct));
        dispatcher.Register<GitStashPushParams, GitStatusDto>("git.stashPush", async (parameters, ct) =>
        {
            var args = new List<string> { "stash", "push" };
            var message = (parameters.Message ?? string.Empty).Trim();
            if (message.Length > 0) { args.Add("-m"); args.Add(message); }
            if (parameters.IncludeUntracked) args.Add("--include-untracked");
            await _runner.MustAsync(args, ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitStashIndexParams, GitStatusDto>("git.stashApply", async (parameters, ct) =>
        {
            await _runner.MustAsync(["stash", "apply", StashRef(parameters.Index)], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitStashIndexParams, GitStatusDto>("git.stashPop", async (parameters, ct) =>
        {
            await _runner.MustAsync(["stash", "pop", StashRef(parameters.Index)], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitStashIndexParams, GitStatusDto>("git.stashDrop", async (parameters, ct) =>
        {
            await _runner.MustAsync(["stash", "drop", StashRef(parameters.Index)], ct, TimeSpan.FromMinutes(2));
            return await StatusAsync(ct);
        });
        dispatcher.Register<GitInstallResult>("git.installGit", InstallAsync);
        // AI commit-message drafting: the user's model writes the message from the diff digest
        // plus the repository's recent commit style.
        dispatcher.Register<GitGenerateParams, string>("git.generateCommitMessage", GenerateCommitMessageAsync);
    }

    /// <summary>Builds the diff digest prompt and asks the injected model for one commit message.</summary>
    private async Task<string> GenerateCommitMessageAsync(GitGenerateParams parameters, CancellationToken ct)
    {
        // The dispatcher hands a null payload when the caller sends no params.
        var apiConfigId = parameters?.ApiConfigId;
        if (complete == null) throw new InvalidOperationException("当前宿主未提供模型，无法生成提交信息；请在「模型与 API」中配置后再试。");
        if (!await _runner.IsRepositoryAsync(ct)) throw new InvalidOperationException("当前工作区不是 Git 仓库。");
        var status = await _runner.StatusAsync(ct);
        if (status.Count == 0) throw new InvalidOperationException("没有可提交的改动；先修改文件并暂存，再生成提交信息。");
        // Staged changes win (they define the next commit); otherwise describe the whole worktree.
        var staged = status.Staged.Count > 0;
        var patchArgs = new List<string> { "diff", "--no-color", "--unified=2", "--find-renames" };
        if (staged) patchArgs.Add("--cached");
        var stat = await _runner.RunAsync([.. patchArgs, "--stat"], ct, TimeSpan.FromSeconds(60), 32 * 1024);
        var patch = await _runner.RunAsync(patchArgs, ct, TimeSpan.FromSeconds(60), MaxPromptCharacters);
        var recent = await _runner.LogAsync(0, 10, false, null, null, ct);

        var prompt = new StringBuilder();
        prompt.AppendLine("请为下面的改动写一条 Git 提交信息。");
        prompt.AppendLine("要求：第一行是简洁摘要（不超过 72 个字符、不加句号）；改动较多时空一行后列 2-5 条要点；");
        prompt.AppendLine("语言、格式与下面的「最近提交」保持一致；只输出提交信息本身，不要解释、不要 Markdown 代码块。");
        if (recent.Commits.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("最近提交（风格参考）：");
            foreach (var commit in recent.Commits) prompt.AppendLine("- " + commit.Subject);
        }
        prompt.AppendLine();
        prompt.AppendLine(staged ? "已暂存的改动：" : "工作区改动（尚未暂存）：");
        if (!string.IsNullOrWhiteSpace(stat.Stdout)) prompt.AppendLine(stat.Stdout.Trim());
        prompt.AppendLine(string.IsNullOrWhiteSpace(patch.Stdout) ? "（没有文本差异，可能只包含新增或二进制文件。）" : patch.Stdout);
        if (!staged)
        {
            var untracked = await UntrackedDigestAsync(status.Untracked, ct);
            if (untracked.Length > 0) prompt.AppendLine(untracked);
        }
        if (patch.Truncated || prompt.Length > MaxPromptCharacters)
            prompt.AppendLine("（注意：改动内容过长已截断，放宽描述粒度。）");

        var generated = await complete(prompt.ToString(), apiConfigId, ct);
        var message = CleanDraft(generated);
        if (message.Length == 0) throw new InvalidOperationException("模型没有返回提交信息；请重试或手动填写。");
        return message;
    }

    /// <summary>Head of each untracked file so new files can be described, bounded and binary-safe.</summary>
    private async Task<string> UntrackedDigestAsync(IReadOnlyList<GitFileEntry> files, CancellationToken ct)
    {
        if (files.Count == 0) return string.Empty;
        var digest = new StringBuilder();
        digest.AppendLine();
        digest.AppendLine("未跟踪的新文件：");
        foreach (var entry in files.Take(8))
        {
            digest.AppendLine("--- " + entry.Path + " (新文件) ---");
            var (text, _, note) = await ReadUntrackedAsync(entry.Path, ct);
            if (note != null && text.Length == 0) { digest.AppendLine("（" + note.TrimEnd('。') + "）"); continue; }
            digest.AppendLine(text.Length > 1500 ? text[..1500] + "\n…（已截断）" : text);
        }
        if (files.Count > 8) digest.AppendLine($"…以及另外 {files.Count - 8} 个新文件。");
        return digest.ToString();
    }

    /// <summary>Strips fences, labels and wrapping quotes a model may add around the message.</summary>
    private static string CleanDraft(string raw)
    {
        var value = (raw ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = value.IndexOf('\n');
            if (firstBreak >= 0) value = value[(firstBreak + 1)..];
            var close = value.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0) value = value[..close];
        }
        value = value.Trim();
        foreach (var label in new[] { "提交信息：", "提交信息:", "Commit message:", "commit message:", "Message:" })
            if (value.StartsWith(label, StringComparison.OrdinalIgnoreCase)) value = value[label.Length..].Trim();
        if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '“' && value[^1] == '”')) value = value[1..^1].Trim();
        return value.Length > MaxDraftCharacters ? value[..MaxDraftCharacters] : value;
    }

    private async Task<GitSummaryDto> SummaryAsync(CancellationToken ct)
    {
        if (_runner.Executable == null) return new GitSummaryDto(false, string.Empty, false, workspace, null, false, null, 0, 0, null, 0, 0, 0, 0);
        var version = await _runner.VersionAsync(ct);
        if (!await _runner.IsRepositoryAsync(ct)) return new GitSummaryDto(true, version, false, await _runner.CwdAsync(ct), null, false, null, 0, 0, null, 0, 0, 0, 0);
        var status = await _runner.StatusAsync(ct);
        return new GitSummaryDto(true, version, true, await _runner.CwdAsync(ct), status.Branch, status.Detached, status.Upstream,
            status.Ahead, status.Behind, status.Operation, status.Staged.Count, status.Unstaged.Count, status.Untracked.Count, status.Conflicted.Count);
    }

    private async Task<GitStatusDto> StatusAsync(CancellationToken ct)
    {
        if (!await _runner.IsRepositoryAsync(ct)) throw new InvalidOperationException("当前工作区不是 Git 仓库。");
        var status = await _runner.StatusAsync(ct);
        return new GitStatusDto(status.Branch, status.Detached, status.Upstream, status.Ahead, status.Behind, status.Operation,
            status.Staged, status.Unstaged, status.Untracked, status.Conflicted);
    }

    private async Task<GitDiffDto> DiffAsync(GitDiffParams parameters, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(parameters.Path) ? null : parameters.Path.Trim();
        if (path != null && path.Length > 1024) throw new ArgumentException("路径过长。");
        if (parameters.Commit != null) ValidateHash(parameters.Commit);
        if (path != null && parameters.Commit == null && !parameters.Staged)
        {
            // Untracked files have no diff; synthesize the all-added view from the file itself.
            var status = await _runner.StatusAsync(ct);
            if (status.Untracked.Any(entry => string.Equals(entry.Path, path, StringComparison.Ordinal)))
            {
                var (text, truncated, note) = await ReadUntrackedAsync(path, ct);
                var added = UnifiedDiff.ParseAdded(text);
                return new GitDiffDto(path, null, false, added.Added, added.Removed, added.Truncated || truncated, false,
                    added.Rows, note ?? "未跟踪文件 · 全部显示为新增。");
            }
        }
        var patch = await _runner.DiffAsync(path, parameters.Staged, parameters.Commit, ct);
        return new GitDiffDto(path ?? "全部更改", parameters.Commit, parameters.Staged, patch.Added, patch.Removed, patch.Truncated,
            patch.Binary, patch.Rows, patch.Binary ? "二进制文件，无法显示文本差异。" : null);
    }

    private async Task<(string Text, bool Truncated, string? Note)> ReadUntrackedAsync(string path, CancellationToken ct)
    {
        // git reports --show-toplevel with forward slashes; normalise before the prefix check.
        var root = Path.GetFullPath(await _runner.CwdAsync(ct));
        var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径越界：" + path);
        if (!File.Exists(full)) return (string.Empty, false, "文件不存在或已被删除。");
        var info = new FileInfo(full);
        var length = (int)Math.Min(info.Length, MaxUntrackedBytes);
        var bytes = new byte[length];
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(bytes, read, length - read);
                if (count <= 0) break;
                read += count;
            }
            length = read;
        }
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            if (text.Contains('\0')) return (string.Empty, false, "二进制文件，无法显示文本差异。");
            return info.Length > MaxUntrackedBytes
                ? (text, true, "文件较大，仅显示前 256 KB。")
                : (text, false, null);
        }
        catch (DecoderFallbackException) { return (string.Empty, false, "二进制文件，无法显示文本差异。"); }
    }

    private GitJobDto StartJob(string label, IReadOnlyList<string> args)
    {
        var id = Guid.NewGuid().ToString("N");
        _ = Task.Run(async () =>
        {
            try
            {
                var pending = new[] { string.Empty, string.Empty };
                var emitted = 0;
                var truncated = false;
                void OnOutput(string text, bool isError)
                {
                    var index = isError ? 1 : 0;
                    pending[index] += text;
                    int cut;
                    while ((cut = pending[index].IndexOfAny(['\n', '\r'])) >= 0)
                    {
                        var line = pending[index][..cut].TrimEnd('\r');
                        pending[index] = pending[index][(cut + 1)..];
                        Emit(line, isError);
                    }
                    if (pending[index].Length > 4096) { Emit(pending[index], isError); pending[index] = string.Empty; }
                }
                void Emit(string line, bool isError)
                {
                    if (line.Trim().Length == 0) return;
                    if (emitted >= MaxJobLines)
                    {
                        if (!truncated) { truncated = true; Push("git.output", new { jobId = id, text = "… 输出过多，后续内容已省略。", error = false }); }
                        return;
                    }
                    emitted++;
                    Push("git.output", new { jobId = id, text = line, error = isError });
                }
                var result = await _runner.StreamAsync(args, OnOutput, CancellationToken.None);
                Emit(pending[0], false);
                Emit(pending[1], true);
                var ok = result.Ok;
                var message = ok ? (label + "完成。") : GitRunner.Friendly(result);
                Push("git.done", new { jobId = id, label, ok, message });
            }
            catch (Exception ex)
            {
                Push("git.done", new { jobId = id, label, ok = false, message = ex.Message });
            }
        });
        return new GitJobDto(id, label);
    }

    private async Task<GitInstallResult> InstallAsync(CancellationToken ct)
    {
        if (_runner.Executable != null) return new GitInstallResult(true, "已安装：" + _runner.Executable);
        var winget = EnvironmentDoctor.FindExecutable("winget.exe")
            ?? throw new InvalidOperationException("未找到 winget，无法自动安装 Git。请手动安装 Git for Windows 后重启 AllAgent。");
        var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(winget,
            ["install", "--id", "Git.Git", "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--accept-package-agreements"]),
            TimeSpan.FromMinutes(15), ct);
        var text = result.Stderr + result.Stdout;
        if (result.ExitCode != 0 && !text.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            return new GitInstallResult(false, "winget 安装失败：" + Clip(text));
        // A freshly installed git is not on this process's PATH yet; refresh machine + user entries.
        var refreshed = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" +
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) + ";" +
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("PATH", refreshed);
        var located = _runner.RefreshExecutable();
        return located == null
            ? new GitInstallResult(false, "Git 已安装，但当前进程尚未能定位 git.exe；请重启 AllAgent 后重试。")
            : new GitInstallResult(true, "已安装：" + located);
    }

    private void Push(string eventName, object data)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                eventName,
                streamId = string.Empty,
                timestamp = DateTime.UtcNow.ToString("o"),
                data,
            }, JsonOptions.Default);
            pusher.Push(payload);
        }
        catch (Exception ex) { Console.Error.WriteLine("[GitBridge] push failed: " + ex.Message); }
    }

    private static List<string> Clean(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) throw new ArgumentException("请选择要操作的文件。");
        if (paths.Count > MaxPaths) throw new ArgumentException("一次最多操作 " + MaxPaths + " 个文件。");
        var result = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var trimmed = (path ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length > 1024 || trimmed.Contains('\0')) throw new ArgumentException("路径无效：" + trimmed);
            result.Add(trimmed);
        }
        if (result.Count == 0) throw new ArgumentException("请选择要操作的文件。");
        return result;
    }

    private static List<string> Args(IReadOnlyList<string> head, IReadOnlyList<string> paths)
    {
        var args = new List<string>(head);
        args.AddRange(paths);
        return args;
    }

    private static string ValidateBranch(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 255 || !BranchName.IsMatch(trimmed) || trimmed.StartsWith('-'))
            throw new ArgumentException("分支名称无效：" + name);
        return trimmed;
    }

    private static void ValidateHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || !Hash.IsMatch(hash.Trim())) throw new ArgumentException("提交哈希无效：" + hash);
    }

    private static string StashRef(int index)
    {
        if (index < 0 || index > 9999) throw new ArgumentException("贮藏编号无效。");
        return "stash@{" + index + "}";
    }

    private static string Clip(string text) => text.Length > 2000 ? text[..2000] + "…" : text;
}
