using System.Text.Json;
using LanAgent.Core.Tools;
using Spectre.Console;

namespace LanAgent.Terminal;

internal static class TerminalQuestionPrompt
{
    public static async Task<string?> AskAsync(IAnsiConsole console, TerminalKeyReader reader, AgentQuestion question, CancellationToken ct, Action<Spectre.Console.Rendering.IRenderable>? render = null)
    {
        var host = new SpectrePromptConsole(console, reader);
        if (question.Choices.Count == 0 && render == null)
        {
            var answer = await host.PromptAsync(new TextPrompt<string>(Markup.Escape(TerminalSafeText.Clean(question.Question)) + " › ").AllowEmpty(), ct);
            return string.IsNullOrWhiteSpace(answer) ? null : question.Multiple ? JsonSerializer.Serialize(new[] { answer }) : answer;
        }
        var focus = 0;
        var selected = new HashSet<int>();
        var typed = new System.Text.StringBuilder();
        string? result = null;
        var requestOther = false;
        string? notice = null;
        Markup Render()
        {
            var rows = new List<string> { Markup.Escape(TerminalSafeText.Clean(question.Question)) };
            for (var i = 0; i <= question.Choices.Count; i++)
            {
                var label = i == question.Choices.Count ? "其他 › " + typed : question.Choices[i];
                var marker = question.Multiple ? (selected.Contains(i) ? "[x] " : "[ ] ") : "";
                var row = Markup.Escape(marker + TerminalSafeText.Clean(label));
                rows.Add(i == focus ? "[bold yellow]› " + row + "[/]" : "  " + row);
            }
            rows.Add("[dim]↑↓ 选择 · 多选空格勾选 · 其他行直接输入/粘贴 · Enter 提交 · Esc 跳过[/]");
            if (notice != null) rows.Add("[yellow]" + Markup.Escape(notice) + "[/]");
            return new Markup(string.Join("\n", rows));
        }
        async Task ReadAnswerAsync(Action<Spectre.Console.Rendering.IRenderable> update)
        {
            update(Render());
            while (true)
            {
                var key = await host.Input.ReadKeyAsync(true, ct) ?? default;
                if (key.Key == ConsoleKey.UpArrow) focus = Math.Max(0, focus - 1);
                else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.Tab) focus = (focus + 1) % (question.Choices.Count + 1);
                else if (key.Key == ConsoleKey.Enter)
                {
                    if (focus == question.Choices.Count && string.IsNullOrWhiteSpace(typed.ToString()))
                    {
                        if (render == null) { requestOther = true; break; }
                        notice = "请输入其他回答，再按 Enter 提交。";
                        update(Render()); continue;
                    }
                    var answers = new List<string>();
                    if (question.Multiple)
                    {
                        answers.AddRange(selected.Order().Where(i => i < question.Choices.Count).Select(i => question.Choices[i]));
                        if (selected.Contains(question.Choices.Count) && typed.ToString().Trim().Length > 0) answers.Add(typed.ToString());
                    }
                    else if (focus < question.Choices.Count) answers.Add(question.Choices[focus]);
                    else if (typed.ToString().Trim().Length > 0) answers.Add(typed.ToString());
                    if (answers.Count == 0)
                    {
                        notice = "请先按空格勾选答案，再按 Enter 提交。";
                        update(Render()); continue;
                    }
                    result = question.Multiple ? JsonSerializer.Serialize(answers) : answers[0];
                    break;
                }
                else if (focus == question.Choices.Count)
                {
                    if (key.Key == ConsoleKey.Backspace && typed.Length > 0) typed.Length--;
                    else if (!char.IsControl(key.KeyChar) && typed.Length < 20000) typed.Append(key.KeyChar);
                    if (typed.Length > 0) selected.Add(focus); else selected.Remove(focus);
                }
                else if (question.Multiple && key.Key == ConsoleKey.Spacebar)
                {
                    if (!selected.Add(focus)) selected.Remove(focus);
                }
                update(Render());
            }
        }
        if (render != null) await ReadAnswerAsync(render);
        else await console.Live(Render()).AutoClear(true).StartAsync(context => ReadAnswerAsync(context.UpdateTarget));
        if (requestOther)
        {
            // Close the Live scope before opening a text prompt; never nest dynamic displays.
            var other = await host.PromptAsync(new TextPrompt<string>("其他回答 › ")
                .Validate(value => !string.IsNullOrWhiteSpace(value), "请输入其他回答，再按 Enter 提交。"), ct);
            if (!question.Multiple) return other;
            var answers = selected.Order().Where(i => i < question.Choices.Count).Select(i => question.Choices[i]).Append(other);
            return JsonSerializer.Serialize(answers);
        }
        return result;
    }
}
