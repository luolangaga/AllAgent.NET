using System.Text.Json;
using LanAgent.Core.Tools;

namespace LanAgent.Terminal;

internal static class ComputerUsePresentation
{
    internal static string Mode(string? mode) => mode switch { "BackgroundOnly" or "background" => "后台 UIA", "ForegroundAllowed" or "foreground" => "前台键鼠", "IsolatedSession" or "isolated" => "独立 Windows 环境", _ => mode ?? "" };
    internal static string Action(string? action) => action switch {
        "click" => "点击", "invoke" => "调用按钮", "legacy_default_action" => "执行控件默认动作", "type_text" or "set_value" => "输入文本", "select" => "选择", "toggle" => "切换选项",
        "expand" => "展开", "collapse" => "收起", "scroll_up" => "向上滚动", "scroll_down" => "向下滚动", "double_click" => "双击", "right_click" => "右键点击", "keys" => "发送快捷键", "focus" => "激活窗口", _ => action ?? "操作" };
    private static string Get(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static string Title(string name, string argsJson, string? resultJson = null)
    {
        using var args = JsonDocument.Parse(argsJson);
        var a = args.RootElement;
        var label = name switch {
            "computer_tray" => "查找系统托盘图标", "computer_tray_action" => "操作托盘图标 · " + (Get(a, "action") is "" or "open" ? "打开应用" : Action(Get(a, "action"))),
            "computer_screenshot" => "截取窗口画面", "computer_windows" => "查看已打开的窗口", "computer_tree" => "读取窗口控件", "computer_find" => "查找控件 " + Get(a, "name") + " " + Get(a, "automationId") + " " + Get(a, "role"),
            "computer_wait" => "等待界面状态 " + Get(a, "name") + " " + Get(a, "automationId"),
            "computer_mode" => "选择操作模式 → " + Mode(Get(a, "mode")), "computer_status" => "查看电脑操作状态",
            "computer_focus" => "激活目标窗口", "computer_keys" => "发送快捷键 " + Get(a, "keys"), "computer_action" => Action(Get(a, "action")), _ => name };
        if (resultJson != null)
        {
            using var result = JsonDocument.Parse(resultJson);
            if (result.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var target = Get(data, "targetName"); var title = Get(data, "windowTitle");
                if (target.Length > 0) label += " · “" + ToolResult.Clip(target, 50) + "”";
                if (title.Length > 0) label += " · " + ToolResult.Clip(title, 65);
            }
        }
        return TerminalSafeText.Clean(label.Trim());
    }
    internal static string Summary(string name, JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data)) return "无结果";
        var rows = new List<string>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            rows.Add($"找到 {data.GetArrayLength()} 个窗口");
            foreach (var w in data.EnumerateArray().Take(6)) rows.Add("  " + ToolResult.Clip(Get(w, "name"), 65) + (w.TryGetProperty("processId", out var pid) ? " · PID " + pid : ""));
            if (data.GetArrayLength() > 6) rows.Add("  … 点击查看全部窗口");
            return TerminalSafeText.Clean(string.Join('\n', rows));
        }
        if (data.ValueKind != JsonValueKind.Object) return TerminalSafeText.Clean(data.ToString());
        var mode = Get(data, "mode"); if (mode.Length > 0) rows.Add("模式 · " + Mode(mode));
        if (data.TryGetProperty("icons", out var icons))
        {
            rows.Add($"找到 {icons.GetArrayLength()} 个托盘图标");
            foreach (var icon in icons.EnumerateArray().Take(8)) rows.Add("  " + ToolResult.Clip(Get(icon, "name"), 70));
            if (data.TryGetProperty("overflowControls", out var controls) && controls.GetArrayLength() > 0) rows.Add("隐藏图标 · 可展开托盘溢出面板后再次查找");
        }
        if (name == "computer_screenshot") rows.Add("截图已捕获并交给模型 · " + data.GetProperty("width") + " × " + data.GetProperty("height") + " · " + Get(data, "captureMethod"));
        if (name == "computer_status") rows.Add(data.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True ? "总开关 · 已开启，AI 自动选择模式" : "总开关 · 已关闭");
        if (name == "computer_mode") rows.Add("原因 · " + Get(data, "reason"));
        if (data.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            rows.Add($"{(name == "computer_find" ? "匹配" : "读取")} {elements.GetArrayLength()} 个控件");
            foreach (var e in elements.EnumerateArray().Take(5)) rows.Add("  " + Get(e, "role") + " · " + ToolResult.Clip(Get(e, "name"), 60));
            if (elements.GetArrayLength() > 5) rows.Add("  … 点击查看控件和文本");
            if (data.TryGetProperty("nextOffset", out var offset) && offset.ValueKind == JsonValueKind.Number) rows.Add("可继续读取 · offset=" + offset);
        }
        if (data.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True) rows.Add("已观察到预期界面状态 · " + Get(data, "condition"));
        var executed = Get(data, "executed");
        if (executed.Length > 0)
        {
            rows.Add("实际操作 · " + Action(executed));
            var text = Get(data, "textPreview"); if (text.Length > 0) rows.Add("输入 · “" + ToolResult.Clip(text, 100) + "”");
            var keys = Get(data, "keys"); if (keys.Length > 0) rows.Add("按键 · " + keys);
            if (data.TryGetProperty("observed", out var observed) && observed.ValueKind == JsonValueKind.Object)
            {
                var value = Get(observed, "value"); if (value.Length > 0) rows.Add("读回 · “" + ToolResult.Clip(value, 100) + "”");
                var toggle = Get(observed, "toggleState"); if (toggle.Length > 0) rows.Add("选项状态 · " + toggle);
            }
            rows.Add("动作已执行 · 任务结果需界面验证");
        }
        if (result.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) rows.Add("结果有截断 · 继续定位或查看详情");
        return TerminalSafeText.Clean(string.Join('\n', rows));
    }
}
