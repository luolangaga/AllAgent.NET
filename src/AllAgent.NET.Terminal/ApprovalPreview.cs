using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

internal sealed record ApprovalPreview(string Title, string Target, IReadOnlyList<(string Text, Color Color)> Lines, string Summary)
{
    public Action? RememberForSession { get; init; }
    public static ApprovalPreview Create(ToolApprovalRequest request, string workspace)
    {
        var args = request.Arguments;
        string String(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var path = String("path");
        var rows = new List<(string, Color)>();
        var title = request.Name; var target = path; var summary = "";
        switch (request.Name)
        {
            case "file_read":
                title = "读取文件"; target = path;
                rows.Add(("读取路径：" + path, Color.White)); summary = "确认后读取并向模型返回文件内容"; break;
            case "list_directory":
                title = "列出目录"; target = path;
                rows.Add(("目录路径：" + path, Color.White)); summary = "确认后向模型返回目录条目"; break;
            case "external_directory":
                title = "访问外部目录"; target = String("directory");
                rows.Add(("实际路径：" + path, Color.White)); rows.Add(("操作：" + String("operation"), Color.White));
                summary = "授权仅覆盖所列路径；文件写入与未信任的程序执行按当前权限模式继续校验，可信 Shell 别名仍按主机规则运行。记住仅限当前会话。"; break;
            case "cancel_agent": title = "取消子 Agent"; target = String("id"); summary = "停止任务和沙箱执行；已有外部效果不会自动回滚，取消后只保留部分输出。"; break;
            case "file_write":
                title = "写入文件"; var content = String("content"); string? original = null;
                try { var policy = new WorkspacePolicy(workspace); var safe = request.ExternalPathApproved ? policy.ResolveForApproval(path) : policy.Resolve(path); if (File.Exists(safe)) original = File.ReadAllText(safe); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { summary = "路径检查：" + ex.Message; }
                if (original == null) { rows.AddRange(LinesOf(content).Select(line => ("+ " + line, Color.Green))); summary = (summary.Length > 0 ? summary + "\n" : "") + $"创建文件 · {LinesOf(content).Count} 行 · {content.Length:N0} 字符"; }
                else { rows.AddRange(Diff(original, content)); summary = $"覆盖已有文件 · {LinesOf(original).Count} → {LinesOf(content).Count} 行 · {content.Length:N0} 字符"; }
                break;
            case "file_edit":
                title = "修改文件";
                if (args.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var (edit, index) in edits.EnumerateArray().Select((e, i) => (e, i)))
                    {
                        rows.Add(($"替换 {index + 1}", Color.Grey));
                        rows.AddRange(LinesOf(edit.GetProperty("old_string").GetString() ?? "").Select(line => ("- " + line, Color.Red)));
                        rows.AddRange(LinesOf(edit.GetProperty("new_string").GetString() ?? "").Select(line => ("+ " + line, Color.Green)));
                    }
                }
                summary = "基于同一原文件匹配；歧义、重叠或任一替换失败时不写入；兼容换行及有限空白/Unicode 差异"; break;
            case "run_shell":
                title = "运行受控 Shell"; target = String("exe");
                rows.Add(("实际程序：" + (request.ResolvedTarget ?? target), Color.White)); rows.Add(("工作目录：" + (String("working_directory") is { Length: > 0 } cwd ? cwd : "."), Color.Grey));
                if (args.TryGetProperty("args", out var argv)) foreach (var (argument, index) in argv.EnumerateArray().Select((a, i) => (a.GetString() ?? "", i))) rows.Add(($"参数[{index}]：{argument}", Color.White));
                summary = request.Preapproved
                    ? "可信程序 · 参数独立传递 · 超时与输出限制"
                    : "程序不在可信列表 · 本次确认后执行 · 参数独立传递 · 超时与输出限制"; break;
            default:
                title = request.Name.StartsWith("mcp_", StringComparison.Ordinal) ? "调用 MCP 服务" : "执行主机工具";
                target = request.Name;
                foreach (var property in args.EnumerateObject()) rows.Add((property.Name + ": " + Display(property.Value), Color.White));
                summary = "外部工具行为由已配置服务提供；本次授权会写入审计记录"; break;
        }
        if (!string.IsNullOrWhiteSpace(request.Source) && request.Name != "external_directory") summary += " · " + request.Source;
        return new(title, target, rows, summary) { RememberForSession = request.RememberForSession };
    }
    public IRenderable Render(int selected = 1, int scroll = 0, bool expanded = false, int? maximumLines = null)
    {
        var count = maximumLines is { } compact ? (expanded ? Math.Min(6, compact + 4) : compact) : Math.Clamp(AnsiConsole.Profile.Height - 15, 1, expanded ? 24 : 9);
        var rows = new List<IRenderable> { new Text(Title + " · " + Target, new Style(Color.Yellow)).Crop(), new Text(Summary, new Style(Color.Grey)).Crop() };
        foreach (var (text, color) in Lines.Skip(scroll).Take(count)) rows.Add(new Text(text, new Style(color)).Crop());
        if (Lines.Count > count) rows.Add(new Text($"预览 {scroll + 1}–{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} 行 · Ctrl+↑↓ 滚动 · Tab 展开/收起", new Style(Color.Grey)));
        rows.Add(new Markup(selected == 0 ? "[bold green]❯ 1. 允许这一次[/]" : "  1. 允许这一次"));
        rows.Add(new Markup(selected == 1 ? "[bold yellow]❯ 2. 拒绝[/]" : "  2. 拒绝"));
        if (RememberForSession != null) rows.Add(new Markup(selected == 2 ? "[bold green]❯ 3. 此会话记住目录[/]" : "  3. 此会话记住目录"));
        rows.Add(new Text("↑↓ 选择 · Enter 确认 · Y 允许 · N/Esc 拒绝" + (RememberForSession != null ? " · A 记住目录" : "") + " · Ctrl+C 取消任务", new Style(Color.Grey)).Crop());
        return new Rows(rows);
    }
    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Display)),
        JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(p => p.Name + "=" + Display(p.Value))), _ => value.ToString()
    };
    private static IReadOnlyList<string> LinesOf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private static IEnumerable<(string, Color)> Diff(string oldText, string newText)
    {
        var oldLines = LinesOf(oldText); var newLines = LinesOf(newText); var prefix = 0;
        while (prefix < Math.Min(oldLines.Count, newLines.Count) && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0; while (suffix < Math.Min(oldLines.Count, newLines.Count) - prefix && oldLines[oldLines.Count - suffix - 1] == newLines[newLines.Count - suffix - 1]) suffix++;
        foreach (var line in oldLines.Skip(Math.Max(0, prefix - 2)).Take(Math.Min(prefix, 2))) yield return ("  " + line, Color.Grey);
        foreach (var line in oldLines.Skip(prefix).Take(oldLines.Count - prefix - suffix)) yield return ("- " + line, Color.Red);
        foreach (var line in newLines.Skip(prefix).Take(newLines.Count - prefix - suffix)) yield return ("+ " + line, Color.Green);
        foreach (var line in newLines.Skip(newLines.Count - suffix).Take(2)) yield return ("  " + line, Color.Grey);
    }
}
