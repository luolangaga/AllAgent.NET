using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Drives the Git bridge against a real temporary repository; skipped when git is missing.</summary>
public sealed class GitBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allagent-git-" + Guid.NewGuid().ToString("N"));
    private readonly BridgeDispatcher _dispatcher = new();
    private readonly List<string> _frames = [];

    public GitBridgeTests()
    {
        Directory.CreateDirectory(_root);
        var pusher = new BridgeEventPusher();
        pusher.SetPusher(json => { lock (_frames) _frames.Add(json); });
        new GitBridge(_root, pusher).Register(_dispatcher);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort cleanup */ }
    }

    private static bool GitAvailable => GitRunner.Locate() != null;

    private GitRunner Runner() => new(_root);

    private async Task<JsonElement> InvokeAsync(string method, object? parameters = null)
    {
        var json = await _dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        if (error.ValueKind == JsonValueKind.String) throw new InvalidOperationException(error.GetString());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> InvokeExpectingErrorAsync(string method, object? parameters = null)
    {
        var json = await _dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(JsonValueKind.String, error.ValueKind);
        return error.Clone();
    }

    private async Task InitRepoAsync(bool withIdentity = true)
    {
        var runner = Runner();
        await runner.MustAsync(["-c", "init.defaultBranch=main", "init"], default);
        if (withIdentity)
        {
            await runner.MustAsync(["config", "user.name", "AllAgent Test"], default);
            await runner.MustAsync(["config", "user.email", "test@example.com"], default);
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello\n");
        await runner.MustAsync(["add", "-A"], default);
        await runner.MustAsync(["commit", "-m", "initial commit"], default);
    }

    private async Task<string> WaitForFrameAsync(string eventName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (_frames)
            {
                var match = _frames.FirstOrDefault(frame => frame.Contains($"\"{eventName}\""));
                if (match != null) return match;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Did not receive " + eventName);
    }

    [Fact]
    public async Task SummaryAndStatusReportBranchCountsAndFiles()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "untracked\n");

        var summary = await InvokeAsync("git.summary");
        Assert.True(summary.GetProperty("available").GetBoolean());
        Assert.True(summary.GetProperty("repo").GetBoolean());
        Assert.Equal("main", summary.GetProperty("branch").GetString());
        Assert.Equal(0, summary.GetProperty("staged").GetInt32());
        Assert.Equal(1, summary.GetProperty("unstaged").GetInt32());
        Assert.Equal(1, summary.GetProperty("untracked").GetInt32());
        Assert.Equal("git version", summary.GetProperty("version").GetString()![..11]);

        var status = await InvokeAsync("git.status");
        var unstaged = status.GetProperty("unstaged").EnumerateArray().Single();
        Assert.Equal("a.txt", unstaged.GetProperty("path").GetString());
        Assert.Equal("M", unstaged.GetProperty("worktree").GetString());
        var untracked = status.GetProperty("untracked").EnumerateArray().Single();
        Assert.Equal("new.txt", untracked.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("operation").ValueKind);
    }

    [Fact]
    public async Task StageCommitDiffAndLogRoundTrip()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "second line\n");

        var staged = await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });
        Assert.Single(staged.GetProperty("staged").EnumerateArray());
        Assert.Empty(staged.GetProperty("unstaged").EnumerateArray());

        var diff = await InvokeAsync("git.diff", new { path = "a.txt", staged = true });
        Assert.Equal(1, diff.GetProperty("added").GetInt32());
        Assert.Equal(0, diff.GetProperty("removed").GetInt32());
        var lines = diff.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Contains(lines, line => line.GetProperty("kind").GetString() == "added" && line.GetProperty("text").GetString() == "second line");

        var committed = await InvokeAsync("git.commit", new { message = "add second line" });
        Assert.Empty(committed.GetProperty("staged").EnumerateArray());
        Assert.Empty(committed.GetProperty("unstaged").EnumerateArray());

        var log = await InvokeAsync("git.log", new { skip = 0, take = 10, all = false });
        var commits = log.GetProperty("commits").EnumerateArray().ToArray();
        Assert.Equal(2, commits.Length);
        Assert.Equal("add second line", commits[0].GetProperty("subject").GetString());
        Assert.False(log.GetProperty("more").GetBoolean());

        var show = await InvokeAsync("git.show", new { hash = commits[0].GetProperty("hash").GetString() });
        Assert.Equal("add second line", show.GetProperty("commit").GetProperty("subject").GetString());
        Assert.Contains(show.GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "a.txt");
    }

    [Fact]
    public async Task DiffOfAnUntrackedFileIsSynthesizedAsAddedLines()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "fresh.txt"), "one\ntwo\n");

        var diff = await InvokeAsync("git.diff", new { path = "fresh.txt", staged = false });
        Assert.True(diff.GetProperty("added").GetInt32() == 2);
        Assert.Contains("未跟踪", diff.GetProperty("note").GetString());
        var lines = diff.GetProperty("lines").EnumerateArray().ToArray();
        Assert.All(lines, line => Assert.Equal("added", line.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task BranchesSwitchCreateDeleteAndMergeStates()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();

        var branches = await InvokeAsync("git.branches");
        Assert.Equal("main", branches.GetProperty("current").GetString());
        Assert.Contains(branches.GetProperty("local").EnumerateArray(), branch => branch.GetProperty("name").GetString() == "main" && branch.GetProperty("current").GetBoolean());

        var created = await InvokeAsync("git.switch", new { name = "feature/panel", create = true, startPoint = (string?)null });
        Assert.Equal("feature/panel", created.GetProperty("branch").GetString());

        var back = await InvokeAsync("git.switch", new { name = "main", create = false, startPoint = (string?)null });
        Assert.Equal("main", back.GetProperty("branch").GetString());

        await InvokeAsync("git.deleteBranch", new { name = "feature/panel", force = false });
        var after = await InvokeAsync("git.branches");
        Assert.DoesNotContain(after.GetProperty("local").EnumerateArray(), branch => branch.GetProperty("name").GetString() == "feature/panel");

        var invalid = await InvokeExpectingErrorAsync("git.switch", new { name = "--upload-pack=evil", create = false });
        Assert.Contains("分支名称无效", invalid.GetString());
    }

    [Fact]
    public async Task CommittingWithoutIdentityReportsAnActionableError()
    {
        if (!GitAvailable) return;
        // A machine-wide identity would make the commit succeed; only the bare case is testable here.
        var globalIdentity = await Runner().RunAsync(["config", "--global", "--get", "user.email"], default);
        if (globalIdentity.Ok) return;
        await InitRepoAsync(withIdentity: false);
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "more\n");
        await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });

        var error = await InvokeExpectingErrorAsync("git.commit", new { message = "should fail" });
        Assert.Contains("提交身份未设置", error.GetString());
    }

    [Fact]
    public async Task StashPushAndPopRestoreTheWorkingTree()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "wip\n");

        var pushed = await InvokeAsync("git.stashPush", new { message = "wip check", includeUntracked = false });
        Assert.Empty(pushed.GetProperty("unstaged").EnumerateArray());

        var stashes = await InvokeAsync("git.stashList");
        var stash = stashes.EnumerateArray().Single();
        Assert.Equal(0, stash.GetProperty("index").GetInt32());
        Assert.Contains("wip check", stash.GetProperty("message").GetString());

        var popped = await InvokeAsync("git.stashPop", new { index = 0 });
        Assert.Single(popped.GetProperty("unstaged").EnumerateArray());
        Assert.Empty((await InvokeAsync("git.stashList")).EnumerateArray());
    }

    [Fact]
    public async Task GenerateCommitMessageFeedsTheModelDiffAndRepoStyle()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "generated line\n");

        var dispatcher = new BridgeDispatcher();
        string? captured = null;
        string? capturedConfig = null;
        new GitBridge(_root, new BridgeEventPusher(), (prompt, configId, _) => { captured = prompt; capturedConfig = configId; return Task.FromResult("```\nfeat: describe the new line\n```"); }).Register(dispatcher);
        var json = await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "draft", method = "git.generateCommitMessage", @params = new { apiConfigId = "cfg-selected" } }));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        // Fences a model may wrap the message in are stripped before it reaches the textarea.
        Assert.Equal("feat: describe the new line", document.RootElement.GetProperty("data").GetString());
        // The page's selected model config reaches the completion callback unchanged.
        Assert.Equal("cfg-selected", capturedConfig);

        Assert.NotNull(captured);
        Assert.Contains("initial commit", captured);        // recent subjects feed the style reference
        Assert.Contains("generated line", captured);        // unstaged diff digest
        Assert.Contains("工作区改动", captured);
    }

    [Fact]
    public async Task GenerateCommitMessageUsesStagedChangesAndReportsEmptyWorktrees()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        var dispatcher = new BridgeDispatcher();
        string? captured = null;
        new GitBridge(_root, new BridgeEventPusher(), (prompt, _, _) => { captured = prompt; return Task.FromResult("chore: staged only"); }).Register(dispatcher);

        // A clean worktree has nothing to describe.
        var empty = await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "empty", method = "git.generateCommitMessage" }));
        using (var document = JsonDocument.Parse(empty))
            Assert.Contains("没有可提交的改动", document.RootElement.GetProperty("error").GetString());

        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "staged line\n");
        await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });
        var stagedRun = await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "staged", method = "git.generateCommitMessage" }));
        using (var document = JsonDocument.Parse(stagedRun))
            Assert.Equal("chore: staged only", document.RootElement.GetProperty("data").GetString());
        Assert.Contains("已暂存的改动", captured);
    }

    [Fact]
    public async Task LogCarriesParentsAndFiltersByRefAndText()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "branch work\n");
        await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });
        await InvokeAsync("git.commit", new { message = "branch work" });
        await InvokeAsync("git.switch", new { name = "feature/graph", create = true, startPoint = (string?)null });
        await File.WriteAllTextAsync(Path.Combine(_root, "graph.txt"), "graph\n");
        await InvokeAsync("git.stage", new { paths = new[] { "graph.txt" } });
        await InvokeAsync("git.commit", new { message = "graph lane work" });
        await InvokeAsync("git.switch", new { name = "main", create = false, startPoint = (string?)null });

        var all = await InvokeAsync("git.log", new { skip = 0, take = 20, all = true });
        var commits = all.GetProperty("commits").EnumerateArray().ToArray();
        Assert.Equal(3, commits.Length);
        Assert.Contains(commits, commit => commit.GetProperty("refs").EnumerateArray().Any(refValue => refValue.GetString()!.Contains("feature/graph")));
        // Topologically ordered history: every parent either follows on the page or predates it.
        Assert.Contains(commits, commit => commit.GetProperty("parents").GetArrayLength() >= 1);
        Assert.Contains(commits, commit => commit.GetProperty("subject").GetString() == "graph lane work");

        var byRef = await InvokeAsync("git.log", new { skip = 0, take = 20, all = false, @ref = "feature/graph" });
        // feature/graph branched from main after two commits, so it carries three in total.
        Assert.Equal(3, byRef.GetProperty("commits").GetArrayLength());

        var byText = await InvokeAsync("git.log", new { skip = 0, take = 20, all = true, grep = "lane" });
        var filtered = byText.GetProperty("commits").EnumerateArray().Single();
        Assert.Equal("graph lane work", filtered.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task CommitWithPathsIncludesOnlyTheCheckedSelection()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "unchecked change\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "checked file\n");
        // a.txt is pre-staged: the path selection must drop it from the index before committing.
        await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });

        var committed = await InvokeAsync("git.commit", new { message = "only b", paths = new[] { "b.txt" } });
        Assert.Empty(committed.GetProperty("staged").EnumerateArray());
        Assert.Contains(committed.GetProperty("unstaged").EnumerateArray(), entry => entry.GetProperty("path").GetString() == "a.txt");

        var log = await InvokeAsync("git.log", new { skip = 0, take = 5, all = false });
        var latest = log.GetProperty("commits").EnumerateArray().First();
        var show = await InvokeAsync("git.show", new { hash = latest.GetProperty("hash").GetString() });
        var files = show.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()).ToArray();
        Assert.Equal(new[] { "b.txt" }, files);
    }

    [Fact]
    public async Task CommitIncludesStagedDeletionsWhenSelected()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "gone.txt"), "bye\n");
        await InvokeAsync("git.stage", new { paths = new[] { "gone.txt" } });
        await InvokeAsync("git.commit", new { message = "add gone" });

        File.Delete(Path.Combine(_root, "gone.txt"));
        await InvokeAsync("git.stage", new { paths = new[] { "gone.txt" } });
        // A staged deletion exists in neither the index nor the worktree: a path-limited add used
        // to fail with "fatal: pathspec ... did not match any files" and blocked the commit.
        var status = await InvokeAsync("git.commit", new { message = "remove gone", paths = new[] { "gone.txt" } });
        Assert.Empty(status.GetProperty("staged").EnumerateArray());
        var log = await InvokeAsync("git.log", new { skip = 0, take = 3, all = false });
        Assert.Equal("remove gone", log.GetProperty("commits").EnumerateArray().First().GetProperty("subject").GetString());
    }

    [Fact]
    public async Task CommitWithAStalePathExplainsTheStaleSelection()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "keep me\n");

        // A path that vanished from the change list (committed/deleted elsewhere) must not surface
        // git's raw "fatal: pathspec ... did not match any files".
        var stale = await InvokeExpectingErrorAsync("git.commit", new { message = "stale", paths = new[] { "gone.txt" } });
        Assert.Contains("已不在更改列表", stale.GetString());

        // Unstaging an unknown path is a harmless no-op in git; it must not throw.
        var afterUnstage = await InvokeAsync("git.unstage", new { paths = new[] { "gone.txt" } });
        Assert.Single(afterUnstage.GetProperty("unstaged").EnumerateArray());
    }

    [Fact]
    public async Task UndoLastCommitKeepsChangesStaged()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "undo me\n");
        await InvokeAsync("git.stage", new { paths = new[] { "a.txt" } });
        await InvokeAsync("git.commit", new { message = "to be undone" });

        var undone = await InvokeAsync("git.undoLastCommit");
        Assert.Single(undone.GetProperty("staged").EnumerateArray());
        var log = await InvokeAsync("git.log", new { skip = 0, take = 5, all = false });
        Assert.Equal("initial commit", log.GetProperty("commits").EnumerateArray().First().GetProperty("subject").GetString());
    }

    [Fact]
    public async Task FetchRunsAsAStreamedJobAndReportsCompletion()
    {
        if (!GitAvailable) return;
        await InitRepoAsync();
        var job = await InvokeAsync("git.fetch");
        Assert.False(string.IsNullOrEmpty(job.GetProperty("id").GetString()));

        var done = JsonDocument.Parse(await WaitForFrameAsync("git.done"));
        Assert.True(done.RootElement.GetProperty("data").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ReportsNotARepositoryWithoutThrowing()
    {
        if (!GitAvailable) return;
        var summary = await InvokeAsync("git.summary");
        Assert.True(summary.GetProperty("available").GetBoolean());
        Assert.False(summary.GetProperty("repo").GetBoolean());

        var error = await InvokeExpectingErrorAsync("git.status");
        Assert.Contains("不是 Git 仓库", error.GetString());

        var initialized = await InvokeAsync("git.init");
        Assert.True(initialized.GetProperty("branch").ValueKind is JsonValueKind.String or JsonValueKind.Null);
    }
}
