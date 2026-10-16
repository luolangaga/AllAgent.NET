using System.Text.Json;
using LanAgent.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

internal static class PowerShellStorePanel
{
    public static async Task RunAsync(PowerShellModuleStore store, TerminalUi ui, CancellationToken ct)
    {
        string status = "";
        var refresh = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (refresh)
            {
                try { await ManagementUi.Busy("验证已安装 PowerShell 模块", token => store.ExecuteAsync("list", ct: token), ct); status = "能力清单已刷新，退出面板后同步给 AI。"; }
                catch (Exception ex) when (Recoverable(ex, ct)) { status = "刷新失败：" + ex.Message; }
                refresh = false;
            }
            using var catalog = JsonDocument.Parse(store.Summary);
            var modules = catalog.RootElement.EnumerateArray().Select(m => m.Clone()).ToArray();
            var table = new Table().Border(TableBorder.Simple).AddColumn("模块").AddColumn("版本").AddColumn("命令").AddColumn("状态");
            var visibleModules = Math.Clamp(AnsiConsole.Profile.Height - 17, 3, 8);
            foreach (var module in modules.Take(visibleModules))
                table.AddRow(new Text(module.GetProperty("name").GetString()!), new Text(module.GetProperty("version").GetString()!), new Text(module.GetProperty("commandCount").ToString()),
                    new Text(module.GetProperty("error").ValueKind == JsonValueKind.Null ? "可用" : "导入失败"));
            var body = new Rows(new Text($"已发现 {modules.Length} 个模块 · 来源 PowerShell Gallery\n目录：{store.Root}\n"), table,
                new Text((modules.Length > visibleModules ? "列表仅预览前 " + visibleModules + " 个，下方可选择全部模块。\n" : "") + status));
            var choices = new List<string> { "返回聊天", "搜索模块", "按名称安装", "刷新能力清单" };
            choices.AddRange(modules.Select(m => "查看模块 · " + m.GetProperty("name").GetString() + " · " + m.GetProperty("version").GetString()));
            var selected = await Choose("PowerShell 能力商店", choices, body, ct);
            if (selected == "返回聊天") return;
            if (selected == "刷新能力清单") { refresh = true; continue; }
            try
            {
                if (selected == "搜索模块") { await SearchAsync(store, ui, ct); refresh = true; }
                else if (selected == "按名称安装")
                {
                    var name = (await ManagementUi.Text("模块名称")).Trim();
                    var version = (await ManagementUi.Text("版本（留空安装当前稳定版）", empty: true)).Trim();
                    await InstallAsync(store, ui, name, version.Length == 0 ? null : version, ct);
                    refresh = true;
                }
                else await CommandsAsync(store, modules[choices.IndexOf(selected) - 4], ct);
            }
            catch (Exception ex) when (Recoverable(ex, ct)) { status = "操作失败：" + ex.Message; }
        }
    }

    private static async Task SearchAsync(PowerShellModuleStore store, TerminalUi ui, CancellationToken ct)
    {
        var query = (await ManagementUi.Text("搜索 PSGallery 模块")).Trim();
        if (query.Length == 0) return;
        var json = await ManagementUi.Busy("正在搜索 PowerShell Gallery", token => store.ExecuteAsync("search", "*" + query + "*", ct: token), ct);
        using var results = JsonDocument.Parse(json);
        var items = results.RootElement.EnumerateArray().Select(m => m.Clone()).ToArray();
        while (true)
        {
            var choices = new List<string> { "返回商店" };
            choices.AddRange(items.Select(m => m.GetProperty("Name").GetString() + " · " + m.GetProperty("Version").GetString()));
            var body = new Text(items.Length == 0 ? "没有找到模块，请返回后更换关键词。" : $"关键词：{query}\n找到 {items.Length} 个结果（最多显示 30 个），选择模块查看详情。");
            var selected = await Choose("PSGallery 搜索结果", choices, body, ct);
            if (selected == "返回商店") return;
            var item = items[choices.IndexOf(selected) - 1];
            var name = item.GetProperty("Name").GetString()!;
            var version = item.GetProperty("Version").GetString()!;
            var detail = new Text($"{name} · {version}\n作者：{item.GetProperty("Author").GetString()}\n\n{item.GetProperty("Description").GetString()}\n\nhttps://www.powershellgallery.com/packages/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}");
            if (await InstallAsync(store, ui, name, version, ct, detail)) return;
        }
    }

    private static async Task<bool> InstallAsync(PowerShellModuleStore store, TerminalUi ui, string name, string? version, CancellationToken ct, IRenderable? detail = null)
    {
        var body = new Rows(detail ?? new Text(name + " · " + (version ?? "当前稳定版")),
            new Text($"\n安装到：{store.Root}\n将下载模块及依赖、接受相关许可证，并导入验证。"));
        if (await Choose("安装 PowerShell 模块", ["返回", "安装"], body, ct) != "安装") return false;
        await ManagementUi.Busy("安装 " + name + " 并验证导出命令", token => store.ExecuteAsync("install", name, version, token), ct);
        ManagementUi.Success(ui, "已安装 " + name + "，能力清单已更新。");
        return true;
    }

    private static async Task CommandsAsync(PowerShellModuleStore store, JsonElement module, CancellationToken ct)
    {
        var name = module.GetProperty("name").GetString()!;
        var offset = 0;
        var pageSize = Math.Clamp(AnsiConsole.Profile.Height - 14, 4, 16);
        while (true)
        {
            using var page = JsonDocument.Parse(JsonSerializer.Serialize(store.Commands(name, offset, pageSize)));
            var commands = page.RootElement.EnumerateArray().Where(m => m.GetProperty("version").GetString() == module.GetProperty("version").GetString())
                .SelectMany(m => m.GetProperty("commands").EnumerateArray().Select(c => c.GetString()!)).Distinct().ToArray();
            var total = module.GetProperty("commandCount").GetInt32();
            var error = module.GetProperty("error").ValueKind == JsonValueKind.Null ? "可通过 .ps1 脚本供 AI 使用。" : "导入失败：" + module.GetProperty("error").GetString();
            var body = new Text($"{name} · {module.GetProperty("version").GetString()}\n{error}\n\n" + string.Join('\n', commands) + $"\n\n命令 {Math.Min(offset + 1, total)}–{Math.Min(offset + pageSize, total)} / {total}");
            var choices = new List<string> { "返回商店" };
            if (offset > 0) choices.Add("上一页");
            if (offset + pageSize < total) choices.Add("下一页");
            var selected = await Choose("模块与导出命令", choices, body, ct);
            if (selected == "返回商店") return;
            offset += selected == "下一页" ? pageSize : -pageSize;
        }
    }

    private static Task<string> Choose(string title, IReadOnlyList<string> choices, IRenderable body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FullScreenTerminal.Current is { } screen) return screen.ChooseAsync(title, choices, value => value, ct, body: body);
        TerminalOutput.Write(new Panel(new SafeTerminalRenderable(body)).Header(Markup.Escape(title)).Border(BoxBorder.Rounded));
        return Task.FromResult(ManagementUi.Choose(title, choices.ToArray()));
    }

    private static bool Recoverable(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested && ex is ArgumentException or IOException or HttpRequestException or JsonException or InvalidOperationException or TimeoutException;
}
