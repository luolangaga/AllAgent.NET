using LanAgent.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class TerminalMarkdownTests
{
    private static string Render(string markdown, int width = 80)
    {
        var console = new TestConsole(); console.Profile.Width = width;
        console.Write(TerminalMarkdown.Render(markdown)); return console.Output;
    }
    [Theory]
    [InlineData("| 功能 | 结果 |\n| --- | ---: |\n| **目录** | `成功` |")]
    [InlineData("功能 | 结果\n:--- | :---:\n**目录** | `成功`")]
    public void TablesRenderBordersHeadersAndStyledCells(string markdown)
    {
        var output = Render(markdown);
        Assert.Contains("功能", output); Assert.Contains("结果", output); Assert.Contains("目录", output); Assert.Contains("成功", output);
        Assert.Contains("╭", output); Assert.Contains("│", output); Assert.DoesNotContain("---", output); Assert.DoesNotContain("**", output);
    }
    [Fact]
    public void EscapedAndCodePipesRemainInTheirCellsAndMissingCellsAreAllowed()
    {
        var output = Render("| One | Two |\n| --- | --- |\n| a\\|b | `c|d` |\n| short |\n\nAfter");
        Assert.Contains("a|b", output); Assert.Contains("c|d", output); Assert.Contains("short", output); Assert.Contains("After", output);
    }
    [Fact]
    public void CodeFencesAndOrdinaryPipesAreNotTables()
    {
        var output = Render("```text\n| A | B |\n| --- | --- |\n```\n\na | b\njust text");
        Assert.Contains("| --- | --- |", output); Assert.Contains("a | b just text", output); Assert.DoesNotContain("┼", output);
    }
    [Fact]
    public void LongCellsWrapInsideNarrowTerminal()
    {
        var output = Render("| 功能 | 实测结果 |\n| --- | --- |\n| file_read | " + string.Concat(Enumerable.Repeat("读取文件成功", 12)) + " |", 40);
        Assert.Contains("file_read", output); Assert.Contains("读取", output);
        Assert.All(output.Replace("\r", "").Split('\n'), line => Assert.True(line.Sum(character => character is >= '\u4e00' and <= '\u9fff' ? 2 : 1) <= 40));
    }
}
