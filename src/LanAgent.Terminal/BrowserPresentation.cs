using System.Text.Json;
using LanAgent.Core.Tools;

namespace LanAgent.Terminal;

internal static class BrowserPresentation
{
    internal static string Badge(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("operation").GetString() switch
            {
                "input" or "fill_fields" => "TYPE", "click" or "act" => "CLICK", "click_and_wait" or "wait" => "WAIT",
                "state" or "observe" => "PAGE", "locate" => "FIND", "screenshot" => "SHOT", "evaluate" => "JS",
                "open" or "navigate" or "new_tab" => "OPEN", _ => "BROWSER"
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return "BROWSER"; }
    }
    internal static string Title(string json)
    {
        try
        {
            using var call = JsonDocument.Parse(json); var root = call.RootElement;
            var op = root.TryGetProperty("operation", out var operation) ? operation.GetString() ?? "" : "";
            using var details = JsonDocument.Parse(root.TryGetProperty("arguments_json", out var args) ? args.GetString() ?? "{}" : "{}");
            var a = details.RootElement;
            string Get(string key) => a.TryGetProperty(key, out var v) ? v.ToString() : "";
            var target = Get("ref"); if (target.Length > 0) target = "引用 " + target[..Math.Min(8, target.Length)];
            else if (a.TryGetProperty("index", out var index)) target = "元素 #" + index;
            var label = op switch
            {
                "open" or "navigate" => "↗ 打开网页 " + Get("url"), "new_tab" => "＋ 新建标签页 " + Get("url"),
                "click" or "act" => "◉ " + (Get("action") == "input" ? "输入文本（内容隐藏）" : Get("action") == "select" ? "选择选项" : "点击元素") + " " + target,
                "click_and_wait" => "◉ 点击并等待结果 " + target,
                "input" => "✎ 输入文本 " + target + "（内容隐藏）", "fill_fields" => "✎ 批量填表 " + (a.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array ? fields.GetArrayLength() : 0) + " 个字段",
                "locate" => "⌖ 定位元素 " + Get("role") + " " + Get("name") + " " + Get("selector"),
                "state" => "≡ 读取页面状态", "observe" => "≡ 读取页面区域 " + Get("selector"),
                "evaluate" => "ƒ 执行网页 JavaScript（脚本见详情）", "screenshot" => "▣ 截取网页画面",
                "switch_tab" or "switch" => "⇄ 切换标签页 " + Get("tabId"), "close_tab" => "× 关闭标签页 " + Get("tabId"),
                "wait" => "◷ 等待页面 " + Get("seconds") + " 秒", "search" => "⌕ 搜索网页 " + Get("query"),
                "scroll" => "↕ 滚动页面 " + Get("direction"), "keys" => "⌨ 发送按键 " + Get("text"),
                "status" => "≡ 查看浏览器连接", "close" => "× 断开浏览器会话", "reload" => "↻ 刷新页面", _ => "浏览器 · " + op + " " + target
            };
            return TerminalSafeText.Clean(ToolResult.Clip(label.Trim(), 180));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return "浏览器 · 参数解析失败"; }
    }
}
