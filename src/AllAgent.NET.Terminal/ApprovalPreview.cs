using System.Text.Json;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;
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
                title = L.Pick("读取文件", "Read file"); target = path;
                rows.Add((L.Pick("读取路径：", "Read path: ") + path, Color.White)); summary = L.Pick("确认后读取并向模型返回文件内容", "After approval, read the file and return its content to the model"); break;
            case "list_directory":
                title = L.Pick("列出目录", "List directory"); target = path;
                rows.Add((L.Pick("目录路径：", "Directory path: ") + path, Color.White)); summary = L.Pick("确认后向模型返回目录条目", "After approval, return directory entries to the model"); break;
            case "external_directory":
                title = L.Pick("访问外部目录", "Access external directory"); target = String("directory");
                rows.Add((L.Pick("实际路径：", "Actual path: ") + path, Color.White)); rows.Add((L.Pick("操作：", "Operation: ") + String("operation"), Color.White));
                summary = L.Pick("授权仅覆盖所列路径；文件写入与未信任的程序执行按当前权限模式继续校验，可信 Shell 别名仍按主机规则运行。记住仅限当前会话。", "Approval covers only the listed paths; writes and untrusted programs still follow the current permission mode, and trusted shell aliases keep running under host rules. Remember applies only to this conversation."); break;
            case "cancel_agent": title = L.Pick("取消子 Agent", "Cancel child agent"); target = String("id"); summary = L.Pick("停止任务和沙箱执行；已有外部效果不会自动回滚，取消后只保留部分输出。", "Stop the task and sandbox execution; external effects are not rolled back, and only partial output is kept."); break;
            case "file_write":
                title = L.Pick("写入文件", "Write file"); var content = String("content"); string? original = null;
                try { var policy = new WorkspacePolicy(workspace); var safe = request.ExternalPathApproved ? policy.ResolveForApproval(path) : policy.Resolve(path); if (File.Exists(safe)) original = File.ReadAllText(safe); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { summary = L.Pick("路径检查：", "Path check: ") + ex.Message; }
                if (original == null) { rows.AddRange(LinesOf(content).Select(line => ("+ " + line, Color.Green))); summary = (summary.Length > 0 ? summary + "\n" : "") + L.Pick($"创建文件 · {LinesOf(content).Count} 行 · {content.Length:N0} 字符", $"Create file · {LinesOf(content).Count} lines · {content.Length:N0} characters"); }
                else { rows.AddRange(Diff(original, content)); summary = L.Pick($"覆盖已有文件 · {LinesOf(original).Count} → {LinesOf(content).Count} 行 · {content.Length:N0} 字符", $"Overwrite existing file · {LinesOf(original).Count} → {LinesOf(content).Count} lines · {content.Length:N0} characters"); }
                break;
            case "file_edit":
                title = L.Pick("修改文件", "Edit file");
                if (args.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var (edit, index) in edits.EnumerateArray().Select((e, i) => (e, i)))
                    {
                        rows.Add((L.Pick($"替换 {index + 1}", $"Replacement {index + 1}"), Color.Grey));
                        rows.AddRange(LinesOf(edit.GetProperty("old_string").GetString() ?? "").Select(line => ("- " + line, Color.Red)));
                        rows.AddRange(LinesOf(edit.GetProperty("new_string").GetString() ?? "").Select(line => ("+ " + line, Color.Green)));
                    }
                }
                summary = L.Pick("基于同一原文件匹配；歧义、重叠或任一替换失败时不写入；兼容换行及有限空白/Unicode 差异", "Matches against the same original file; no write when ambiguous, overlapping, or any replacement fails; tolerates line-ending and limited whitespace/Unicode differences"); break;
            case "run_shell":
                title = L.Pick("运行受控 Shell", "Run controlled shell"); target = String("exe");
                rows.Add((L.Pick("实际程序：", "Actual program: ") + (request.ResolvedTarget ?? target), Color.White)); rows.Add((L.Pick("工作目录：", "Working directory: ") + (String("working_directory") is { Length: > 0 } cwd ? cwd : "."), Color.Grey));
                if (args.TryGetProperty("args", out var argv)) foreach (var (argument, index) in argv.EnumerateArray().Select((a, i) => (a.GetString() ?? "", i))) rows.Add((L.Pick($"参数[{index}]：", $"Argument[{index}]: ") + argument, Color.White));
                summary = request.Preapproved
                    ? L.Pick("可信程序 · 参数独立传递 · 超时与输出限制", "Trusted program · arguments passed separately · timeout and output limits")
                    : L.Pick("程序不在可信列表 · 本次确认后执行 · 参数独立传递 · 超时与输出限制", "Program not in the trusted list · runs after this approval · arguments passed separately · timeout and output limits"); break;
            default:
                title = request.Name.StartsWith("mcp_", StringComparison.Ordinal) ? L.Pick("调用 MCP 服务", "Call MCP service") : L.Pick("执行主机工具", "Run host tool");
                target = request.Name;
                foreach (var property in args.EnumerateObject()) rows.Add((property.Name + ": " + Display(property.Value), Color.White));
                summary = L.Pick("外部工具行为由已配置服务提供；本次授权会写入审计记录", "External tool behavior comes from the configured service; this approval is written to the audit log"); break;
        }
        if (!string.IsNullOrWhiteSpace(request.Source) && request.Name != "external_directory") summary += " · " + request.Source;
        return new(title, target, rows, summary) { RememberForSession = request.RememberForSession };
    }
    public IRenderable Render(int selected = 1, int scroll = 0, bool expanded = false, int? maximumLines = null)
    {
        var count = maximumLines is { } compact ? (expanded ? Math.Min(6, compact + 4) : compact) : Math.Clamp(AnsiConsole.Profile.Height - 15, 1, expanded ? 24 : 9);
        var rows = new List<IRenderable> { new Text(Title + " · " + Target, new Style(Color.Yellow)).Crop(), new Text(Summary, new Style(Color.Grey)).Crop() };
        foreach (var (text, color) in Lines.Skip(scroll).Take(count)) rows.Add(new Text(text, new Style(color)).Crop());
        if (Lines.Count > count) rows.Add(new Text(L.Pick($"预览 {scroll + 1}–{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} 行 · Ctrl+↑↓ 滚动 · Tab 展开/收起", $"Preview {scroll + 1}-{Math.Min(scroll + count, Lines.Count)} / {Lines.Count} lines · Ctrl+↑↓ scroll · Tab expand/collapse"), new Style(Color.Grey)));
        rows.Add(new Markup(selected == 0 ? L.Pick("[bold green]❯ 1. 允许这一次[/]", "[bold green]❯ 1. Allow this once[/]") : L.Pick("  1. 允许这一次", "  1. Allow this once")));
        rows.Add(new Markup(selected == 1 ? L.Pick("[bold yellow]❯ 2. 拒绝[/]", "[bold yellow]❯ 2. Reject[/]") : L.Pick("  2. 拒绝", "  2. Reject")));
        if (RememberForSession != null) rows.Add(new Markup(selected == 2 ? L.Pick("[bold green]❯ 3. 此会话记住目录[/]", "[bold green]❯ 3. Remember this directory for the conversation[/]") : L.Pick("  3. 此会话记住目录", "  3. Remember this directory for the conversation")));
        rows.Add(new Text(L.Pick("↑↓ 选择 · Enter 确认 · Y 允许 · N/Esc 拒绝", "↑↓ select · Enter confirm · Y allow · N/Esc reject") + (RememberForSession != null ? L.Pick(" · A 记住目录", " · A remember directory") : "") + L.Pick(" · Ctrl+C 取消任务", " · Ctrl+C cancel task"), new Style(Color.Grey)).Crop());
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
