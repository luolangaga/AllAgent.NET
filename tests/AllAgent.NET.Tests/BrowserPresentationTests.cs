using System.Text.Json;
using AllAgent.NET.Terminal;
using AllAgent.NET.Core.Services;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class BrowserPresentationTests
{
    [Fact]
    public void RenderedCodeActBlockShowsOperationTargetAndMessage()
    {
        var args = JsonSerializer.Serialize(new { operation = "click", arguments_json = "{\"index\":3}" });
        var block = new CodeExecutionBlock("code", "print(call_tool('browser', operation='click', arguments_json='{}'))");
        block.Start(new("click-1", "browser", args));
        block.Complete(new("click-1", "browser", "{\"ok\":true,\"data\":{\"browser\":{\"name\":\"外置 Edge\"},\"target\":\"保存\",\"actionMessages\":[{\"text\":\"保存失败：请填写所有字段\"}]}}"));
        var console = new TestConsole(); console.Profile.Width = 160;
        console.Write(block);
        Assert.Contains("CLICK", console.Output); Assert.Contains("点击元素", console.Output);
        Assert.Contains("保存", console.Output); Assert.Contains("请填写所有字段", console.Output);
    }

    [Theory]
    [InlineData("click", "CLICK", "点击元素")]
    [InlineData("input", "TYPE", "输入文本")]
    [InlineData("state", "PAGE", "读取页面状态")]
    [InlineData("click_and_wait", "WAIT", "点击并等待")]
    [InlineData("screenshot", "SHOT", "截取网页")]
    [InlineData("evaluate", "JS", "JavaScript")]
    public void BrowserActionsHaveSpecificLabelsWithoutLeakingEnteredText(string operation, string badge, string label)
    {
        var args = JsonSerializer.Serialize(new { operation, arguments_json = "{\"index\":3,\"text\":\"private-password\"}" });
        Assert.Equal(badge, BrowserPresentation.Badge(args));
        Assert.Contains(label, TerminalUi.ToolLabel("browser", args));
        Assert.DoesNotContain("private-password", CodeExecutionBlock.ToolSubject("browser", args, "browser"));
    }
}
