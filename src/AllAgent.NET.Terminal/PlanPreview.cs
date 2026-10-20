using AllAgent.NET.Core.Models;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

/// <summary>Terminal presentation of a proposed plan. Markdown is rendered directly; the
/// browser uses the same Markdown to build its HTML card.</summary>
internal sealed record PlanPreview(PlanProposal Plan)
{
    public IReadOnlyList<string> Lines { get; } = Plan.Markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public IRenderable Render(int selected = 1, int scroll = 0, bool expanded = false, int? maximumLines = null)
    {
        var count = maximumLines is { } compact
            ? expanded ? Math.Min(12, compact + 8) : compact
            : Math.Clamp(AnsiConsole.Profile.Height - 15, 1, expanded ? 30 : 12);
        var window = string.Join('\n', Lines.Skip(scroll).Take(count));
        var rows = new List<IRenderable>
        {
            new Text(Plan.Title, new Style(Color.Yellow)).Crop(),
            new Text(L.Pick("批准后将切换到完全访问（写文件、执行命令、访问工作区外），并立即开始执行。", "Approval switches to full access (write files, run commands, access outside the workspace) and starts execution immediately."), new Style(Color.Grey)).Crop(),
            TerminalMarkdown.Render(window)
        };
        if (Lines.Count > count)
            rows.Add(new Text(L.Pick($"预览 {scroll + 1}–{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} 行 · Ctrl+↑↓ 滚动 · Tab 展开/收起", $"Preview {scroll + 1}-{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} lines · Ctrl+↑↓ scroll · Tab expand/collapse"), new Style(Color.Grey)).Crop());
        rows.Add(new Markup(selected == 0 ? L.Pick("[bold green]❯ 1. 批准并自动执行[/]", "[bold green]❯ 1. Approve and execute[/]") : L.Pick("  1. 批准并自动执行", "  1. Approve and execute")));
        rows.Add(new Markup(selected == 1 ? L.Pick("[bold yellow]❯ 2. 拒绝，继续规划[/]", "[bold yellow]❯ 2. Reject and keep planning[/]") : L.Pick("  2. 拒绝，继续规划", "  2. Reject and keep planning")));
        rows.Add(new Text(L.Pick("↑↓ 选择 · Enter 确认 · Y 批准 · N/Esc 拒绝 · Ctrl+↑↓ 滚动 · Tab 展开", "↑↓ select · Enter confirm · Y approve · N/Esc reject · Ctrl+↑↓ scroll · Tab expand"), new Style(Color.Grey)).Crop());
        return new Rows(rows);
    }
}
