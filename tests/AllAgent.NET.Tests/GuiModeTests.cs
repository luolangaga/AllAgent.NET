using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>The GUI mode entry point: argument spelling, window URL and one-window-per-data-directory.</summary>
public sealed class GuiModeTests
{
    [Theory]
    [InlineData("-guimode")]
    [InlineData("--guimode")]
    [InlineData("--gui")]
    [InlineData("-gui")]
    public void GuiModeFlagsSelectTheGuiCommand(string flag)
    {
        var args = TerminalArguments.Parse([flag]);
        Assert.True(args.GuiMode);
        Assert.Equal("gui", args.Command);
        Assert.Empty(args.Positionals);
    }

    [Fact]
    public void GuiCommandWordKeepsTheHostInTheForeground()
    {
        var args = TerminalArguments.Parse(["gui"]);
        Assert.Equal("gui", args.Command);
        Assert.False(args.GuiMode);
    }

    [Fact]
    public void GuiModeRefusesToShareTheCommandLineWithOtherCommands()
    {
        Assert.Throws<ArgumentException>(() => TerminalArguments.Parse(["run", "-guimode", "任务"]));
        var gui = TerminalArguments.Parse(["gui", "-guimode"]);
        Assert.Equal("gui", gui.Command); Assert.True(gui.GuiMode);
    }

    [Fact]
    public void OptionsCarryTheirValuesAndUnknownSingleDashTokensStayPositional()
    {
        var args = TerminalArguments.Parse(["gui", "--gui-url", "http://127.0.0.1:5173/", "-gui-devtools"]);
        Assert.Equal("http://127.0.0.1:5173/", args.Get("gui-url"));
        Assert.Contains("gui-devtools", args.Flags);
        var positional = TerminalArguments.Parse(["run", "-not-a-registered-option"]);
        Assert.Equal(["-not-a-registered-option"], positional.Positionals);
        Assert.Throws<ArgumentException>(() => TerminalArguments.Parse(["--unknown-option"]));
    }

    [Fact]
    public void WindowUrlPinsTheBridgeSocketAndRespectsAnExistingQuery()
    {
        Assert.Equal("http://127.0.0.1:52518/?bridge=ws%3A%2F%2F127.0.0.1%3A52518%2Fbridge", GuiShell.Url(null, 52518));
        Assert.Equal("http://127.0.0.1:5173/?bridge=ws%3A%2F%2F127.0.0.1%3A4000%2Fbridge", GuiShell.Url("http://127.0.0.1:5173/", 4000));
        Assert.Equal("http://127.0.0.1:5173/?theme=dark&bridge=ws%3A%2F%2F127.0.0.1%3A4001%2Fbridge", GuiShell.Url("http://127.0.0.1:5173/?theme=dark", 4001));
    }

    [Fact]
    public void WindowTitleNamesTheWorkspace()
    {
        Assert.Equal("AllAgent.NET · LanAgent", GuiShell.Title(@"C:\Users\luolan\LanAgent"));
        Assert.Equal("AllAgent.NET · LanAgent", GuiShell.Title(@"C:\Users\luolan\LanAgent\"));
        Assert.Equal("AllAgent.NET", GuiShell.Title(@"C:\"));
    }

    [Fact]
    public void OnlyOneWindowMayOwnADataDirectory()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "gui-instance");
        Assert.True(GuiShell.TryAcquireInstance(directory));
        Assert.False(GuiShell.TryAcquireInstance(directory));
        Assert.False(GuiShell.TryAcquireInstance(directory.ToUpperInvariant()));
        Assert.True(GuiShell.TryAcquireInstance(Path.Combine(workspace.Root, "gui-instance-two")));
    }
}
