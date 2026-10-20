using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed record SelectionItem<T>(T Value, string? Heading = null);

internal static class TerminalSelectionPrompt
{
    internal static SelectionPrompt<SelectionItem<T>> Create<T>(string title, IEnumerable<T> choices,
        Func<T, string> label, int pageSize, Style highlight, Func<T, string>? group = null, Func<T, string>? color = null)
    {
        var prompt = new SelectionPrompt<SelectionItem<T>>()
            .Title(Markup.Escape(TerminalSafeText.Clean(title)))
            .PageSize(Math.Max(3, pageSize)).HighlightStyle(highlight)
            .Mode(SelectionMode.Leaf)
            .UseConverter(item =>
            {
                var text = Markup.Escape(TerminalSafeText.Clean(item.Heading ?? label(item.Value)));
                return color == null ? text : $"[{color(item.Value)}]●[/] " + text;
            })
            .EnableSearch().SearchPlaceholderText(L.Pick("[dim]输入关键字搜索 · ↑↓ 选择 · Enter 确认 · Esc 返回[/]", "[dim]Type to search · ↑↓ select · Enter confirm · Esc back[/]"));
        if (group == null) prompt.AddChoices(choices.Select(value => new SelectionItem<T>(value)));
        else
            foreach (var section in choices.GroupBy(group))
                prompt.AddChoiceGroup(new SelectionItem<T>(section.First(), section.Key),
                    section.Select(value => new SelectionItem<T>(value)));
        prompt.CancelResult = () => throw new OperationCanceledException(L.Pick("已返回。", "Returned."));
        return prompt;
    }
}
