using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

/// <summary>One output owner: interactive widgets go to the full screen, CLI output keeps its normal stream.</summary>
internal static class TerminalOutput
{
    public static void Write(IRenderable renderable)
    {
        renderable = new SafeTerminalRenderable(renderable);
        if (FullScreenTerminal.Current is { } screen) screen.Append(renderable); else AnsiConsole.Write(renderable);
    }
    public static void WriteLine() => Write(new Text("\n"));
    public static void MarkupLine(string markup) => Write(new Markup(markup + "\n"));
}
