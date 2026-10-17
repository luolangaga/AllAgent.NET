using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ThinkingLineTests
{
    [Fact]
    public void EachRunningInvocationHasItsOwnIndicatorAfterItsOutput()
    {
        var block = new CodeExecutionBlock("code", "print(call_tool('file_read', path='first.txt'))\nprint(call_tool('file_read', path='second.txt'))");
        block.Start(new("first", "file_read", "{\"path\":\"first.txt\"}"));
        block.Start(new("second", "file_read", "{\"path\":\"second.txt\"}"));
        string Render()
        {
            var console = new TestConsole(); console.Profile.Width = 100; console.Write(block); return console.Output;
        }
        var running = Render();
        Assert.Equal(2, running.Split("正在执行工具").Length - 1);
        Assert.True(running.IndexOf("first.txt", StringComparison.Ordinal) < running.IndexOf("正在执行工具", StringComparison.Ordinal));
        block.Complete(new("first", "file_read", ToolResult.Success(new { content = "first result" }).ToJson()));
        Assert.Equal(1, Render().Split("正在执行工具").Length - 1);
        block.Complete(new("second", "file_read", ToolResult.Success(new { content = "second result" }).ToJson()));
        Assert.DoesNotContain("正在执行工具", Render());
    }

    [Fact]
    public void RunningToolsShowDotsAndRemoveIndicatorOnCompletion()
    {
        var block = new CodeExecutionBlock("code", "print(1)");
        var active = new TestConsole(); active.Profile.Width = 80; active.Write(block);
        Assert.Contains("正在执行工具", active.Output);
        Assert.Contains(Spectre.Console.Spinner.Known.Dots.Frames, frame => active.Output.Contains(frame));
        block.Complete(new("code", "execute_code", ToolResult.Success(new { stdout = "1" }).ToJson()));
        var completed = new TestConsole(); completed.Profile.Width = 80; completed.Write(block);
        Assert.DoesNotContain("正在执行工具", completed.Output);
    }
    [Fact]
    public void CountStaysAtRightEdgeForShortAndLongPreviews()
    {
        var console = new TestConsole(); console.Profile.Width = 160;
        console.Write(new ThinkingLine("读取配置\n检查路径", 0, hint: " [Ctrl+O 展开]"));
        Assert.Contains("检查路径", console.Output);
        Assert.EndsWith("（2行） [Ctrl+O 展开]", console.Output);
        Assert.Equal(160, new Segment(console.Output).CellCount());
        var shortOutput = console.Output;
        console.Clear();
        console.Write(new ThinkingLine(new string('x', 300) + "\n" + new string('y', 300), 0, hint: " [Ctrl+O 展开]"));
        Assert.Equal(new Segment(shortOutput[..shortOutput.IndexOf("（2行）", StringComparison.Ordinal)]).CellCount(),
            new Segment(console.Output[..console.Output.IndexOf("（2行）", StringComparison.Ordinal)]).CellCount());
    }

    [Fact]
    public async Task ToolsAndNextIterationKeepTaskThinkingAndRetriesDiscardOnlyCurrentAttempt()
    {
        var ui = new TerminalUi(false, false);
        string Activity()
        {
            var output = new TestConsole(); output.Profile.Width = 100;
            output.Write(ui.RenderActivity()); return output.Output;
        }
        await ui.RenderAsync(new AgentIteration(1, 0, 0), default);
        await ui.RenderAsync(new ThinkingDelta("读取配置\n检查路径"), default);
        await ui.RenderAsync(new ToolCallStarted("code", "execute_code", "{\"code\":\"print(1)\"}"), default);
        Assert.Matches("检查路径 +（2行）", Activity());
        await ui.RenderAsync(new ToolCallCompleted("code", "execute_code", ToolResult.Success(new { stdout = "1", exitCode = 0 }).ToJson()), default);
        await ui.RenderAsync(new AgentIteration(2, 0, 1), default);
        Assert.Matches("检查路径 +（2行）", Activity());
        Assert.DoesNotContain("等待模型", Activity());
        await ui.RenderAsync(new ThinkingDelta("失败尝试"), default);
        await ui.RenderAsync(new AgentNotice("重试") { ResetResponse = true }, default);
        Assert.DoesNotContain("失败尝试", Activity());
        Assert.Matches("检查路径 +（2行）", Activity());
        await ui.RenderAsync(new ThinkingDelta("检查完成\n准备回答"), default);
        Assert.Matches("准备回答 +（4行）", Activity());
    }

    [Theory]
    [InlineData(45, 18)]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public async Task FooterShowsConfiguredEffortAndUpdatesAfterChanges(int width, int height)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var ui = new TerminalUi(false, false);
        var config = new ApiConfig { ProviderId = "deepseek", DefaultModel = "deepseek-flash", ReasoningEffort = "high" };
        ui.SetModelContext(config);
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, catalog, mcp, ui, console);
        string Frame()
        {
            var output = new TestConsole(); output.Profile.Width = width; output.Profile.Height = height;
            output.Write(screen.BuildFrame(width, height)); return output.Output;
        }
        Assert.Contains("思考强度 high", Frame());
        config.ReasoningEffort = "low"; ui.SetModelContext(config);
        Assert.Contains("思考强度 low", Frame());
        config.DefaultModel = "other-model"; config.ReasoningEffort = "default"; ui.SetModelContext(config);
        Assert.Contains("思考强度 default", Frame());
        ui.SetThinkingEffort(null);
        Assert.Contains("思考强度 未设置", Frame());
    }

    [Fact]
    public async Task ActivityShowsOnlyActualStreamedReasoning()
    {
        var ui = new TerminalUi(false, false);
        var waiting = new TestConsole(); waiting.Profile.Width = 100;
        waiting.Write(ui.RenderActivity());
        Assert.DoesNotContain("等待模型返回", waiting.Output);
        Assert.DoesNotContain("（0行）", waiting.Output);
        await ui.RenderAsync(new ThinkingDelta("检查文件\r\n正在分析最新内容\r\n"), default);
        var streaming = new TestConsole(); streaming.Profile.Width = 100;
        streaming.Write(ui.RenderActivity());
        Assert.Contains("正在分析最新内容", streaming.Output);
        Assert.Contains("（2行）", streaming.Output);
        ui.ShowThinking = false;
        var hidden = new TestConsole(); hidden.Write(ui.RenderActivity());
        Assert.DoesNotContain("正在分析最新内容", hidden.Output);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(80)]
    [InlineData(120)]
    public void ReasoningRemainsOneRowWithCountAndScrollsWithoutBreakingUnicode(int width)
    {
        var thought = "first line\r\n第二行\n" + string.Concat(Enumerable.Repeat("检查路径🙂e\u0301 ", 40));
        string Render(long time)
        {
            var console = new TestConsole(); console.Profile.Width = width;
            console.Write(new ThinkingLine(thought, time)); return console.Output;
        }
        var initial = Render(0); var scrolled = Render(2500);
        Assert.Contains("（3行）", initial); Assert.Contains("（3行）", scrolled);
        Assert.DoesNotContain('\n', initial); Assert.DoesNotContain('\r', initial);
        Assert.NotEqual(initial, scrolled);
        Assert.True(new Segment(initial).CellCount() <= width);
        Assert.DoesNotContain('\ufffd', scrolled);
    }

    [Fact]
    public void ThinkingAndCodeKeepLineCountsAndFullInspectorContent()
    {
        var block = new CodeExecutionBlock("thought", "first\n第二行\nlast", reasoning: true);
        var preview = new TestConsole(); preview.Profile.Width = 100; preview.Write(block);
        Assert.Contains("（3行）", preview.Output); Assert.Contains("Ctrl+O", preview.Output);
        Assert.DoesNotContain("计算与处理", preview.Output);
        var details = new TestConsole(); details.Write(block.Details(-1, 0));
        Assert.Contains("first", details.Output); Assert.Contains("第二行", details.Output);
        var codeBlock = new CodeExecutionBlock("code", "print(1)\nprint(2)");
        var code = new TestConsole(); code.Profile.Width = 100; code.Write(codeBlock);
        Assert.DoesNotContain("print(1)", code.Output); // Dispatch code renders only inside the details view.
        var codeDetails = new TestConsole(); codeDetails.Write(codeBlock.Details(-1, 0));
        Assert.Contains("print(1)", codeDetails.Output); Assert.Contains("print(2)", codeDetails.Output);
    }

    [Fact]
    public void FailuresRenderRedWithAnExplicitFailureMarker()
    {
        var console = new TestConsole(); console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor; console.Profile.Capabilities.Ansi = true; console.EmitAnsiSequences = true;
        console.Write(TerminalUi.ToolFailure("未执行 list_directory"));
        console.Write(TerminalUi.ToolFailure("missing file"));
        Assert.Contains("未执行", console.Output); Assert.Contains("missing file", console.Output);
        Assert.DoesNotContain("查看目录", console.Output); Assert.Contains("✕", console.Output);
        Assert.Contains("\u001b[38;5;9m", console.Output);
    }
}
