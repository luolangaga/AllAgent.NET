using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ExecutionExperienceTests
{
    [Fact]
    public void ChildCardUpdatesWithTaskProgressAndKeepsPythonInDetails()
    {
        var task = new BackgroundTaskInfo("child", "检查构建", Guid.NewGuid(), "agent", BackgroundTaskState.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CurrentStep = "正在运行命令", Steps = ["已完成 · 读取文件"] };
        var block = new CodeExecutionBlock("code", "call_tool('start_agent', task='检查构建')") { TaskLookup = _ => task };
        block.Start(new("host", "start_agent", "{\"task\":\"检查构建\"}"));
        block.Complete(new("host", "start_agent", ToolResult.Success(new { taskId = "child" }).ToJson()));
        var firstVersion = block.Version;
        var console = new TestConsole(); console.Profile.Width = 100; console.Write(block);
        Assert.Contains("子代理任务", console.Output); Assert.Contains("正在运行命令", console.Output); Assert.Contains("代码 ›", console.Output); Assert.Contains("(1 行)", console.Output);
        task = task with { State = BackgroundTaskState.Completed, Output = "构建成功", UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        Assert.NotEqual(firstVersion, block.Version);
        var details = new TestConsole(); details.Profile.Width = 100; details.Write(block.Details(0, 0));
        Assert.Contains("构建成功", details.Output); Assert.Contains("已完成", details.Output);
        var code = new TestConsole(); code.Profile.Width = 100; code.Write(block.Details(-1, 0));
        Assert.Contains("call_tool", code.Output);
    }
    [Fact]
    public async Task UnavailableShellIsRejectedBeforeApprovalAndInstalledNpmRunsAfterApproval()
    {
        using var workspace = new TestWorkspace();
        var approved = 0;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TerminalArguments.DiscoverShellExecutables(aliases);
        var registry = new ToolRegistry((_, _) => { approved++; return ValueTask.FromResult(true); });
        registry.Register(new SystemTools(new WorkspacePolicy(workspace.Root), new(workspace.Python, workspace.Pwsh, []), aliases));
        var rejected = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = "missing-shell-for-qa", args = new[] { "--version" } }), default);
        Assert.False(rejected.Ok); Assert.Equal(0, approved); Assert.Contains("not found", rejected.Error);
        Assert.True(aliases.ContainsKey("npm"), "This acceptance test requires the installed npm PowerShell launcher.");
        var executed = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = "npm", args = new[] { "--version" } }), default);
        Assert.True(executed.Ok, executed.ToJson()); Assert.Equal(1, approved);
        Assert.Matches(@"\d+\.\d+\.\d+", JsonSerializer.SerializeToElement(executed.Data).GetProperty("stdout").GetString()!);
        Assert.True(ShellExecutablePolicy.TryResolve(aliases, "npm.ps1", out var resolved)); Assert.Equal(aliases["npm"], resolved);
        Assert.False(ShellExecutablePolicy.TryResolve(aliases, Path.Combine(workspace.Root, "npm.ps1"), out _));
    }

    [Fact]
    public async Task FullAccessPermitsExternalFilesAndSkipsEvenADenyingApprover()
    {
        using var workspace = new TestWorkspace(); using var external = new TestWorkspace();
        var approverCalls = 0;
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true, Approver = (_, _) => { approverCalls++; return ValueTask.FromResult(false); } });
        var path = Path.Combine(external.Root, "outside.txt");
        var result = await runtime.Tools.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path, content = "完全访问验证" }), default);
        Assert.True(result.Ok, result.ToJson()); Assert.Equal(0, approverCalls); Assert.Equal("完全访问验证", await File.ReadAllTextAsync(path));
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspacePolicy(workspace.Root).Resolve(path));
        Assert.Equal(path, new WorkspacePolicy(workspace.Root, true).Resolve(path));
        Assert.True(ShellExecutablePolicy.TryResolve(new Dictionary<string, string>(), workspace.Python, out _, true));
        Assert.False(ShellExecutablePolicy.TryResolve(new Dictionary<string, string>(), workspace.Python, out _));
        Assert.Contains("FULL ACCESS", runtime.CodeAct.Instructions);
    }

    [Fact]
    public void LexerHighlightsMultilineCallsButIgnoresCommentsStringsAndLookalikes()
    {
        var source = "# call_tool('ignored')\ntext = \"call_tool('fake')\"\nxcall_tool('fake')\nr = call_tool(\n  'file_read', path='a(1).txt'\n)\ns = call_tool('list_directory', path='.')";
        var spans = PythonToolSpans.Parse(source);
        Assert.Equal(new[] { "file_read", "list_directory" }, spans.Select(span => span.Name));
        Assert.EndsWith(")", source[spans[0].Start..spans[0].End]); Assert.Contains("\n", source[spans[0].Start..spans[0].End]);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public async Task ClickOpensLiveToolDetailsAndKeepsDraftAndScroll(int width, int height)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new TerminalUi(false, false), console);
        var state = new InputState(); state.Set("保留草稿🙂"); screen.SetInput(state, [], "ready");
        var block = new CodeExecutionBlock("python", "for path in ['甲.txt', '乙.txt']:\n    r = call_tool('file_read', path=path)\n    print(r['ok'])");
        block.Start(new("a", "file_read", "{\"path\":\"甲.txt\"}"));
        block.Start(new("b", "file_read", "{\"path\":\"乙.txt\"}"));
        block.Complete(new("a", "file_read", ToolResult.Success(new { content = "中文结果" }).ToJson()));
        block.Complete(new("b", "file_read", ToolResult.Failure("文件不存在").ToJson()));
        screen.RegisterExecution(block); screen.Append(block);
        screen.RenderFrame(width, height);
        screen.Navigation(new TerminalInput(default, Wheel: 100)); // The nested result cards can exceed a small viewport.
        var segments = screen.RenderFrame(width, height);
        Assert.DoesNotContain(segments, segment => segment.Text.Contains("call_tool"));
        Assert.Contains(segments, segment => segment.Style.Foreground == Color.Green && segment.Text.Contains("甲"));
        Assert.DoesNotContain(segments, segment => block.Markers.Values.Contains(segment.Style.Background));
        var lines = Segment.SplitLines(segments, width, height);
        var y = lines.FindIndex(line => line.Any(segment => segment.Text.Contains("甲")));
        Assert.True(y >= 0);
        var x = 0; foreach (var segment in lines[y]) { if (segment.Text.Contains("甲")) break; x += segment.CellCount(); }
        Assert.True(screen.SelectInput(new(default, Pointer: new(x, y, PointerAction.Down, false)), state));
        string Frame() { var output = new TestConsole(); output.Profile.Width = width; output.Profile.Height = height; output.Write(screen.BuildFrame(width, height)); return output.Output; }
        var initial = Frame(); Assert.Contains("工具执行详情", initial); Assert.Contains("甲.txt", initial); Assert.Contains("中文结果", initial);
        Assert.DoesNotContain("\\u4E2D", initial); Assert.Equal("保留草稿🙂", state.Text);
        Assert.True(screen.InspectorInput(new(new('\0', ConsoleKey.RightArrow, false, false, false))));
        var next = Frame(); Assert.Contains("乙.txt", next); Assert.Contains("失败", next);
        Assert.True(screen.InspectorInput(new(new('\u001b', ConsoleKey.Escape, false, false, false))));
        var closed = Frame(); Assert.DoesNotContain("工具执行详情", closed); Assert.Contains("保留草稿", closed);
    }

    [Fact]
    public void CliRemovesIterationLimitAndFullAccessIsExplicit()
    {
        var normal = TerminalArguments.Parse(["run", "task"]).Options(new(new(false, false)));
        Assert.False(normal.FullAccess); Assert.Equal(Timeout.InfiniteTimeSpan, normal.RunTimeout); Assert.Equal(int.MaxValue, normal.MaxToolCalls);
        Assert.True(TerminalArguments.Parse(["run", "task", "--full-access"]).Options(new(new(false, false))).FullAccess);
        Assert.Throws<ArgumentException>(() => TerminalArguments.Parse(["run", "task", "--max-iterations", "30"]));
    }
}
