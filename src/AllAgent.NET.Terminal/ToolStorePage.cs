using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal static class ToolStorePage
{
    public static Task Skills(SkillCatalog catalog, TerminalUi ui, IReadOnlyList<string> words) => Run(false, ui, words, async (client, item, ct) =>
    {
        var scope = ManagementUi.Choose("安装位置", "project", "user");
        if (!await ManagementUi.Confirm(ui, "下载并安装第三方技能 " + item.Source + "/" + item.Name + "？")) return;
        var name = await ManagementUi.Busy("正在下载技能包…", token => client.InstallSkill(item, catalog, scope, token), ct);
        ManagementUi.Success(ui, "已安装 /" + name + "；可在 /skills 中管理。");
    });
    public static Task Mcp(DataStore store, TerminalUi ui, IReadOnlyList<string> words, CancellationToken ct) => Run(true, ui, words, async (_, item, _) =>
    {
        var config = ToolStoreClient.McpConfig(item);
        TerminalOutput.Write(new Text(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })));
        if (store.McpServerConfigs.Any(c => c.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("此服务已安装，未覆盖现有配置。");
        if (!await ManagementUi.Confirm(ui, "保存此 MCP 服务？默认禁用，不会立即执行或连接。")) return;
        store.AddMcpServerConfig(config);
        ManagementUi.Success(ui, "已安装配置（禁用）。请在 /mcp edit 填写参数和认证，再 enable / connect。");
    }, ct);
    private static async Task Run(bool mcp, TerminalUi ui, IReadOnlyList<string> words, Func<ToolStoreClient, StoreItem, CancellationToken, Task> install, CancellationToken ct = default)
    {
        using var http = ToolStoreClient.CreateHttp(); var client = new ToolStoreClient(http);
        var query = string.Join(' ', words).Trim();
        if (mcp && query.Length == 0) { ManagementUi.Interactive(ui); query = await ManagementUi.Text("搜索 MCP 服务（英文关键词）"); }
        string? cursor = null;
        while (true)
        {
            StoreResults results;
            try { results = await ManagementUi.Busy("正在连接" + (mcp ? "官方 MCP Registry…" : " skills.sh…"), token => query.Length == 0 && !mcp ? client.RecommendSkills(token) : client.Search(mcp, query, cursor, token), ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                if (ui.Json || !ui.Interactive) throw new IOException("商店数据获取失败；没有加载示例或预填数据。" + e.Message, e);
                ui.Error("商店数据获取失败：" + e.Message + "。没有加载预填数据。");
                if (ManagementUi.Choose("连接失败", "返回", "重试") == "重试") continue;
                return;
            }
            ManagementUi.Output(ui, new { source = mcp ? "Official MCP Registry" : "skills.sh", query, results.Basis, fetchedAt = DateTimeOffset.UtcNow, results.Items, results.NextCursor }, () =>
            {
                var home = !mcp && query.Length == 0;
                var table = new Table().Title(home ? "[bold]Skills 商店 · 今日热门[/]" : (mcp ? "[bold]MCP 商店 · 搜索结果[/]" : "[bold]Skills 商店 · 搜索结果[/]")).AddColumn("名称").AddColumn("来源").AddColumn("说明");
                if (home) TerminalOutput.Write(new Text(results.Basis + "\n"));
                foreach (var item in results.Items) table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Source), Markup.Escape(item.Description + (query.Length == 0 && !mcp && item.Data.TryGetProperty("installs", out var count) ? " · 趋势安装量 " + count.ToString() : "")));
                TerminalOutput.Write(table);
                if (results.Items.Count == 0) TerminalOutput.Write(new Text("没有找到结果。请更换关键词。"));
            });
            if (ui.Json || !ui.Interactive) return;
            var actions = new List<string> { "返回", "搜索" };
            if (!mcp) actions.Add(query.Length == 0 ? "刷新推荐" : "返回推荐");
            var itemStart = actions.Count;
            if (results.NextCursor != null) { actions.Add("下一页"); itemStart++; }
            actions.AddRange(results.Items.Select((i, index) => $"{index + 1}. {i.Name} · {i.Source}"));
            var selected = ManagementUi.Choose("商店 · 选择查看详情", actions.ToArray());
            if (selected == "返回") return;
            if (selected is "刷新推荐" or "返回推荐") { query = ""; cursor = null; continue; }
            if (selected == "搜索") { query = await ManagementUi.Text("搜索关键词"); cursor = null; continue; }
            if (selected == "下一页") { cursor = results.NextCursor; continue; }
            var item = results.Items[actions.IndexOf(selected) - itemStart];
            TerminalOutput.Write(new Panel(new Text(item.Name + "\n" + item.Source + "\n" + item.Description + "\n" + item.Url + "\n目录收录不代表安全审核，请先核对发布来源。" )).Header("商店详情"));
            if (ManagementUi.Choose("操作", "返回结果", "安装") == "安装")
            {
                try { await install(client, item, ct); }
                catch (Exception e) when (e is ArgumentException or IOException or HttpRequestException or JsonException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; ui.Error("安装失败：" + e.Message); }
            }
        }
    }
}
