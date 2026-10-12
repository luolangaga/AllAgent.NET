using System.Text.Json;
using LanAgent.Core.Tools;
using Spectre.Console;

namespace LanAgent.Terminal;

internal static class ManagementUi
{
    public static Task<T> Busy<T>(string title, Func<CancellationToken, Task<T>> action, CancellationToken ct = default) => FullScreenTerminal.Current is { } screen ? screen.BusyAsync(title, action, ct) : action(ct);
    public static async Task Busy(string title, Func<CancellationToken, Task> action, CancellationToken ct = default) => await Busy(title, async token => { await action(token); return true; }, ct);
    public static void Output(TerminalUi ui, object value, Action human)
    {
        if (ui.Json) Console.WriteLine(JsonSerializer.Serialize(value, ToolResult.JsonOptions)); else human();
    }
    public static void Success(TerminalUi ui, string text) => Output(ui, new { ok = true, message = text }, () => TerminalOutput.MarkupLine("[green]✓ " + Markup.Escape(text) + "[/]"));
    public static string Require(IReadOnlyList<string> args, int index, string label) => args.ElementAtOrDefault(index) ?? throw new ArgumentException("缺少" + label + "。输入 /help 查看用法。");
    public static async Task<string> Text(string label, string? defaultValue = null, bool secret = false, bool empty = false)
    {
        if (FullScreenTerminal.Current is { } screen) return await screen.TextAsync(label, defaultValue, secret, empty);
        var prompt = new TextPrompt<string>(Markup.Escape(label));
        if (defaultValue is { Length: > 0 }) prompt.DefaultValue(defaultValue);
        if (secret) prompt.Secret(); if (empty) prompt.AllowEmpty();
        return await AnsiConsole.PromptAsync(prompt);
    }
    public static void Interactive(TerminalUi ui) { if (!ui.Interactive || ui.Json) throw new ArgumentException("此操作需要交互终端；命令行模式请填写参数。"); }
    public static async Task<bool> Confirm(TerminalUi ui, string text) => ui.Interactive && !ui.Json && (FullScreenTerminal.Current is { } screen ? await screen.ConfirmAsync(text) : await AnsiConsole.PromptAsync(new ConfirmationPrompt(Markup.Escape(text)) { DefaultValue = false }));
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static string Choose(string title, params string[] choices) => Choose(title, choices, c => c);
    public static T Choose<T>(string title, IEnumerable<T> choices, Func<T,string> label) where T : notnull => FullScreenTerminal.Current is { } screen
        ? screen.Choose(title, choices.ToArray(), label)
        : AnsiConsole.Prompt(new SelectionPrompt<T>().Title(Markup.Escape(title)).UseConverter(c => Markup.Escape(label(c))).AddChoices(choices));
    public static async Task WriteNewJsonAsync(string path, object data)
    {
        await using var file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(file, data, new JsonSerializerOptions(ToolResult.JsonOptions) { WriteIndented = true });
    }
}
