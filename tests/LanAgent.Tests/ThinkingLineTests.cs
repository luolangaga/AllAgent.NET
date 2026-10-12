using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ThinkingLineTests
{
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
        Assert.Contains("(3 行)", initial); Assert.Contains("(3 行)", scrolled);
        Assert.DoesNotContain('\n', initial); Assert.DoesNotContain('\r', initial);
        Assert.NotEqual(initial, scrolled);
        Assert.True(new Segment(initial).CellCount() <= width);
        Assert.DoesNotContain('\ufffd', scrolled);
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
