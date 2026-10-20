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

    public IRenderable Render(int selected = 1, int scroll = 0, bool expanded = false, int? maximumLines = null, InputState? comment = null)
    {
        var count = maximumLines is { } compact
            ? expanded ? Math.Min(12, compact + 8) : compact
            : Math.Clamp(AnsiConsole.Profile.Height - 15, 1, expanded ? 30 : 12);
        var window = string.Join('\n', Lines.Skip(scroll).Take(count));
        var rows = new List<IRenderable>
        {
            new Text(Plan.Title, new Style(Color.Yellow)).Crop(),
            new Text(L.Pick("批准后将切换到完全访问（写文件、执行命令、访问工作区外），并立即开始执行；写下评论则保持计划模式，AI 会按建议持续修订。", "Approval switches to full access (write files, run commands, access outside the workspace) and starts execution immediately; a comment keeps plan mode and the AI keeps revising from it."), new Style(Color.Grey)).Crop(),
            TerminalMarkdown.Render(window)
        };
        if (Lines.Count > count)
            rows.Add(new Text(L.Pick($"预览 {scroll + 1}–{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} 行 · Ctrl+↑↓ 滚动 · Tab 展开/收起", $"Preview {scroll + 1}-{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} lines · Ctrl+↑↓ scroll · Tab expand/collapse"), new Style(Color.Grey)).Crop());
        rows.Add(new Markup(selected == 0 ? L.Pick("[bold green]❯ 1. 批准并自动执行[/]", "[bold green]❯ 1. Approve and execute[/]") : L.Pick("  1. 批准并自动执行", "  1. Approve and execute")));
        rows.Add(new Markup(selected == 1 ? L.Pick("[bold yellow]❯ 2. 拒绝，继续规划[/]", "[bold yellow]❯ 2. Reject and keep planning[/]") : L.Pick("  2. 拒绝，继续规划", "  2. Reject and keep planning")));
        rows.Add(new Markup(selected == 2 ? L.Pick("[bold yellow]❯ 3. 评论 / 建议，继续规划[/]", "[bold yellow]❯ 3. Comment / suggestion, keep planning[/]") : L.Pick("  3. 评论 / 建议，继续规划", "  3. Comment / suggestion, keep planning")));
        if (comment != null)
        {
            rows.Add(new Text(L.Pick("评论 / 建议", "Comment / suggestion"), new Style(Color.Cyan1)));
            rows.Add(new InputText(comment, maxRows: 3));
            rows.Add(new Text(L.Pick("Enter 提交评论并继续规划 · Esc 返回计划选项", "Enter submits the comment and keeps planning · Esc returns to the plan options"), new Style(Color.Grey)).Crop());
        }
        else if (selected == 2)
            rows.Add(new Text(L.Pick("Enter 开始输入评论；提交后 AI 会按建议修订计划。", "Enter starts typing; the AI revises the plan from your comment."), new Style(Color.Grey)).Crop());
        rows.Add(new Text(L.Pick("↑↓ 选择 · Enter 确认 · Y 批准 · N/Esc 拒绝 · 3 评论 · Ctrl+↑↓ 滚动 · Tab 展开", "↑↓ select · Enter confirm · Y approve · N/Esc reject · 3 comment · Ctrl+↑↓ scroll · Tab expand"), new Style(Color.Grey)).Crop());
        return new Rows(rows);
    }
}
