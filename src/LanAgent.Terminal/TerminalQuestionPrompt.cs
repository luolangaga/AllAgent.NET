using System.Text.Json;
using LanAgent.Core.Tools;
using Spectre.Console;

namespace LanAgent.Terminal;

internal static class TerminalQuestionPrompt
{
    private sealed record Choice(string Text, bool Other = false);
    public static async Task<string?> AskAsync(IAnsiConsole console, TerminalKeyReader reader, AgentQuestion question, CancellationToken ct)
    {
        var host = new SpectrePromptConsole(console, reader);
        var choices = question.Choices.Select(text => new Choice(text)).Append(new Choice("输入其他回答", true)).ToArray();
        string Label(Choice choice) => Markup.Escape(TerminalSafeText.Clean(choice.Text));
        List<Choice> selected;
        if (question.Multiple)
        {
            var prompt = new MultiSelectionPrompt<Choice>().Title(Markup.Escape(TerminalSafeText.Clean(question.Question)))
                .PageSize(Math.Clamp(console.Profile.Height - 8, 3, 14)).Required().UseConverter(Label)
                .InstructionsText("[dim]↑↓ 移动 · 空格 勾选 · Enter 提交 · Esc 跳过[/]").AddChoices(choices);
            selected = await host.PromptAsync(prompt, ct);
        }
        else
        {
            var prompt = new SelectionPrompt<Choice>().Title(Markup.Escape(TerminalSafeText.Clean(question.Question)))
                .PageSize(Math.Clamp(console.Profile.Height - 8, 3, 14)).UseConverter(Label).AddChoices(choices);
            selected = [await host.PromptAsync(prompt, ct)];
        }
        var answers = selected.Where(choice => !choice.Other).Select(choice => choice.Text).ToList();
        if (selected.Any(choice => choice.Other))
        {
            var typed = await host.PromptAsync(new TextPrompt<string>("其他回答 › ").AllowEmpty(), ct);
            if (!string.IsNullOrWhiteSpace(typed)) answers.Add(typed);
        }
        return answers.Count == 0 ? null : question.Multiple ? JsonSerializer.Serialize(answers) : answers[0];
    }
}
