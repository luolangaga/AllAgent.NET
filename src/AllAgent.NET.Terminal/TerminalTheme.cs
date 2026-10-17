using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed record TerminalTheme(string Id, string Name, Color Background, Color Surface, Color Accent, Color Foreground)
{
    public static IReadOnlyList<TerminalTheme> All { get; } = [
        new("night", "夜色 · OpenCode 风格", new(17, 19, 26), new(26, 29, 39), new(125, 207, 255), new(220, 225, 235)),
        new("warm", "暖色", new(24, 22, 21), new(36, 31, 28), new(217, 119, 87), new(235, 226, 219)),
        new("light", "浅色", new(245, 245, 247), new(230, 234, 240), new(35, 87, 150), new(35, 40, 48))
    ];
    public static TerminalTheme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
