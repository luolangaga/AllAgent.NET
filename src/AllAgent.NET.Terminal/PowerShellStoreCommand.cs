using System.Text.Json;
using AllAgent.NET.Core.Services;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal static class PowerShellStoreCommand
{
    public static async Task ExecuteAsync(PowerShellModuleStore store, TerminalUi ui, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var operation = args.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        if (operation == "panel")
        {
            ManagementUi.Interactive(ui);
            if (args.Count > 1) throw new ArgumentException("用法 /powershell [panel]");
            await PowerShellStorePanel.RunAsync(store, ui, ct);
            return;
        }
        if (operation == "commands")
        {
            if (args.Count > 3) throw new ArgumentException("用法 powershell commands [模块] [偏移量]");
            var skip = args.Count > 2 ? int.Parse(args[2]) : 0;
            await store.ExecuteAsync("list", ct: ct);
            Show(store.Commands(args.ElementAtOrDefault(1) ?? "", skip, limit: 100));
            return;
        }
        if (args.Count > (operation == "install" ? 3 : operation == "search" ? 2 : 1)) throw new ArgumentException("用法 powershell list|search 关键词|install 模块 [版本]|commands [模块]");
        var name = args.ElementAtOrDefault(1);
        if (operation == "search") name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("用法 powershell search 关键词") : "*" + name + "*";
        var json = await ManagementUi.Busy(operation == "install" ? "从 PSGallery 安装模块及其依赖（可能需要数分钟）" : "查询 PowerShell 能力商店（通常 1–3 秒）", token => store.ExecuteAsync(operation, name, args.ElementAtOrDefault(2), token), ct);
        using var doc = JsonDocument.Parse(json);
        if (operation == "search") Show(doc.RootElement.Clone());
        else Show(new { modules = JsonSerializer.Deserialize<JsonElement>(store.Summary), path = store.Root });
        void Show(object result) => ManagementUi.Output(ui, result, () => TerminalOutput.Write(new Text(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n")));
    }
}
