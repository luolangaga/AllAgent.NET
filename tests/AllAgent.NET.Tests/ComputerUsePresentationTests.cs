using System.Text.Json;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ComputerUsePresentationTests
{
    [Theory]
    [InlineData(55)]
    [InlineData(110)]
    public void WindowCardShowsActualWindowNamesAndCount(int width)
    {
        var block = new CodeExecutionBlock("python", "r = call_tool('computer_windows')\nprint(r)");
        block.Start(new ToolCallStarted("windows", "computer_windows", "{}") { ParentId = "python" });
        block.Complete(new ToolCallCompleted("windows", "computer_windows", """{"ok":true,"data":[{"name":"记事本","processId":100},{"name":"AllAgent 测试窗口","processId":200}]}"""));
        block.Complete(new ToolCallCompleted("python", "execute_code", """{"ok":true,"data":{"hostCallSummary":{"failed":0}}}"""));
        var console = new TestConsole(); console.Profile.Width = width; console.Write(block);
        Assert.Contains("电脑操作", console.Output); Assert.Contains("查看已打开的窗口", console.Output);
        Assert.Contains("找到 2 个窗口", console.Output); Assert.Contains("记事本", console.Output); Assert.Contains("PID 100", console.Output);
        Assert.DoesNotContain("computer_windows · 成功", console.Output);
    }
    [Fact]
    public void ActionCardShowsModeTargetInputAndObservedValue()
    {
        var result = """{"ok":true,"data":{"executed":"set_value","mode":"BackgroundOnly","targetName":"搜索框","targetRole":"Edit","windowTitle":"测试程序","textPreview":"hello","observed":{"value":"hello"},"verificationRequired":true}}""";
        var title = ComputerUsePresentation.Title("computer_action", """{"action":"type_text"}""", result);
        Assert.Contains("搜索框", title); Assert.Contains("测试程序", title); Assert.Contains("输入文本", title);
        using var document = JsonDocument.Parse(result); var summary = TerminalUi.ResultSummary(document.RootElement, false, "computer_action");
        Assert.Contains("后台 UIA", summary); Assert.Contains("输入 · “hello”", summary); Assert.Contains("读回 · “hello”", summary);
        Assert.Contains("任务结果需界面验证", summary);
    }
    [Fact]
    public void HandledHostFailureDoesNotShowSuccessfulExecutionHeader()
    {
        var block = new CodeExecutionBlock("python", "print('handled')");
        block.Complete(new ToolCallCompleted("python", "execute_code", """{"ok":true,"data":{"hostCallSummary":{"failed":1}}}"""));
        var console = new TestConsole(); console.Profile.Width = 100; console.Write(block);
        Assert.Contains("含 1 项操作失败", console.Output); Assert.DoesNotContain("执行步骤 · 成功", console.Output);
    }
    [Fact]
    public void TrayCardShowsIconsAndOverflowDiscovery()
    {
        using var result = JsonDocument.Parse("""{"ok":true,"data":{"mode":"BackgroundOnly","icons":[{"name":"WinIsland - 灵动岛"}],"overflowControls":[{"name":"显示隐藏的图标"}]}}""");
        var text = TerminalUi.ResultSummary(result.RootElement, false, "computer_tray");
        Assert.Contains("1 个托盘图标", text); Assert.Contains("WinIsland", text); Assert.Contains("隐藏图标", text);
    }
}
