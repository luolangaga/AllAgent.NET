using LanAgent.Core.Data;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class TerminalFollowupTests
{
    [Fact]
    public async Task GlobalLinkedPackagesLoadAndTheirReferencesStayInsideResolvedPackage()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var source = Path.Combine(w.Root, "installed", "linked-skill"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: linked-skill\ndescription: linked fixture\n---\nLINKED-PROOF");
        await File.WriteAllTextAsync(Path.Combine(source, "reference.txt"), "REFERENCE-PROOF");
        var global = Path.Combine(w.Root, "home", ".agents", "skills"); Directory.CreateDirectory(global);
        var link = Path.Combine(global, "linked-skill");
        if (OperatingSystem.IsWindows())
        {
            var info = BoundedProcess.StartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command",
                $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{source.Replace("'", "''")}' | Out-Null"]);
            var result = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(10), default);
            Assert.Equal(0, result.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, source);
        try
        {
            var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), Path.Combine(w.Root, "home"));
            var skill = catalog.Get("linked-skill"); Assert.Equal("global", skill.Scope); Assert.Equal(Path.Combine(source, "SKILL.md"), skill.Path);
            Assert.Empty(catalog.Diagnostics); Assert.Contains("LINKED-PROOF", catalog.Invoke("linked-skill", ""));
            Assert.Equal("REFERENCE-PROOF", catalog.ReadReference("linked-skill", "reference.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => catalog.ReadReference("linked-skill", "../outside.txt"));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task GlobalSkillsAreDiscoveredWithProjectPriorityAndPagedLargeInstructions()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        void Skill(string relative, string name, string text)
        {
            var directory = Path.Combine(w.Root, relative); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: fixture\n---\n{text}\n");
        }
        Skill("home/.agents/skills/global-example", "global-example", new string('中', 40000));
        Skill("home/.codex/skills/.system/system-example", "system-example", "SYSTEM-PROOF");
        Skill("home/.codex/skills/codex-example", "codex-example", "CODEX-PROOF");
        Skill("home/.codex/skills/.system/internal-example", "internal-example", "INTERNAL-PROOF");
        Skill("home/.claude/skills/claude-example", "claude-example", "CLAUDE-PROOF");
        Skill("home/.config/opencode/skills/opencode-example", "opencode-example", "OPENCODE-PROOF");
        Skill(".lanagent/skills/system-example", "system-example", "PROJECT-PROOF");
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), Path.Combine(w.Root, "home"));
        Assert.Equal(new[] { "global-example", "system-example" }, catalog.List().Where(skill => skill.Scope != "builtin").Select(skill => skill.Name));
        Assert.Single(catalog.GlobalDirectories);
        Assert.Equal(Path.Combine(w.Root, "home", ".agents", "skills"), catalog.GlobalDirectories[0]);
        Assert.DoesNotContain(catalog.List(), skill => skill.Path.Contains(".codex"));
        Assert.Equal("global", catalog.Get("global-example").Scope);
        Assert.Equal("PROJECT-PROOF", catalog.Get("system-example").Instructions);
        Assert.Empty(catalog.Diagnostics); Assert.Same(catalog.List(), catalog.List());
        var tools = new SkillTools(catalog);
        var result = await tools.ReadAsync("global-example");
        Assert.True(result.Truncated);
        catalog.Enable("global-example", false); Assert.False(catalog.Get("global-example").Enabled);
        var loaded = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), Path.Combine(w.Root, "home")); Assert.False(loaded.Get("global-example").Enabled);
        Assert.True(File.Exists(Path.Combine(w.Root, "home/.agents/skills/global-example/SKILL.md")));
    }

    [Fact]
    public async Task OutputArrowKeysScrollWhileInputArrowsKeepEditing()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, new(store, w.Root, w.Root, w.Root), mcp, new(false, false), console);
        screen.Append(new Text(string.Join('\n', Enumerable.Range(0, 100).Select(i => $"OUTPUT-{i:03}"))));
        var input = new InputState(); input.Set("draft"); screen.SetInput(input, [], "ready");
        var original = screen.RenderFrame(100, 35);
        screen.SelectInput(new(default, Pointer: new(4, 5, PointerAction.Down)), input);
        screen.SelectInput(new(default, Pointer: new(4, 5, PointerAction.Up)), input);
        Assert.True(screen.Navigation(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)));
        var moved = screen.RenderFrame(100, 35); Assert.NotEqual(string.Concat(original.Select(s => s.Text)), string.Concat(moved.Select(s => s.Text)));
        Assert.True(screen.Navigation(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)));
        var lines = Segment.SplitLines(screen.RenderFrame(100, 35), 100, 35);
        var y = lines.FindIndex(line => string.Concat(line.Select(s => s.Text)).Contains("draft"));
        screen.SelectInput(new(default, Pointer: new(5, y, PointerAction.Down)), input);
        Assert.False(screen.Navigation(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)));
        Assert.Equal("draft", input.Text);
    }

    [Fact]
    public void StatusQueryShowsOneLineWithoutRepeatingTheCommandAndCodeKeepsLineCount()
    {
        var task = new BackgroundTaskInfo("fixture-id", "dotnet test DUPLICATE-PROOF", Guid.NewGuid(), "shell", BackgroundTaskState.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var block = new CodeExecutionBlock("code", "r = call_tool('shell_status', id='fixture-id')\nprint(r)") { TaskLookup = _ => task };
        block.Start(new("status", "shell_status", "{\"id\":\"fixture-id\"}"));
        block.Complete(new("status", "shell_status", ToolResult.Success(task).ToJson()));
        var console = new TestConsole(); console.Profile.Width = 100; console.Write(block);
        Assert.Contains("（2行）", console.Output); Assert.Contains("查询已有任务", console.Output);
        Assert.Equal(1, console.Output.Split("查询已有任务").Length - 1); Assert.Contains("fixture-", console.Output);
        Assert.DoesNotContain("执行步骤", console.Output);
        Assert.DoesNotContain("DUPLICATE-PROOF", console.Output); Assert.DoesNotContain("后台命令", console.Output);
        console.Clear(); console.Write(block.Details(0, 0)); Assert.Contains("DUPLICATE-PROOF", console.Output);
    }

    [Fact]
    public async Task ShellStatusWaitsForTheSameTaskWithoutRelaunchAndHonorsCancellation()
    {
        using var w = new TestWorkspace(); await using var jobs = new BackgroundTasks(); var starts = 0;
        async IAsyncEnumerable<AgentEvent> Run(string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { Interlocked.Increment(ref starts); await Task.Delay(200, ct); yield return new AgentDone("stop"); }
        var id = jobs.Start("fixture", Guid.NewGuid(), "shell", Run);
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), jobs: jobs);
        var result = await tools.ShellStatusAsync(id);
        Assert.True(result.Ok); Assert.Equal(BackgroundTaskState.Completed, jobs.Get(id).State); Assert.Equal(1, starts);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.ShellStatusAsync(id, 2, cancel.Token));
        Assert.False((await tools.ShellStatusAsync(id, 61)).Ok);
    }

    [Fact]
    public async Task UnfinishedShellChecksKeepOutputInHostAndCancellingWaitDoesNotCancelJob()
    {
        using var w = new TestWorkspace(); await using var jobs = new BackgroundTasks();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await release.Task.WaitAsync(ct); yield return new AgentDone("stop"); }
        var id = jobs.Start("fixture", Guid.NewGuid(), "shell", Run);
        jobs.AppendOutput(id, "OUTPUT-KEPT-IN-HOST");
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), jobs: jobs);
        Assert.DoesNotContain("OUTPUT-KEPT-IN-HOST", (await tools.ShellStatusAsync(id, 0)).ToJson());
        Assert.Contains("OUTPUT-KEPT-IN-HOST", jobs.Get(id).Output);
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.ShellStatusAsync(id, 60, cancel.Token));
        Assert.Contains(jobs.Get(id).State, new[] { BackgroundTaskState.Queued, BackgroundTaskState.Running });
        release.SetResult();
        Assert.Contains("OUTPUT-KEPT-IN-HOST", (await tools.ShellStatusAsync(id)).ToJson());
    }
}
