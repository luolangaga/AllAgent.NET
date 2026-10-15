using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LanAgent.Core.Tools;

namespace LanAgent.ComputerUse;

internal static class SystemTray
{
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    internal static bool IsTrayRoot(AutomationElement root) => root.Properties.ClassName.ValueOrDefault is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    internal static bool IsOverflow(AutomationElement e)
    {
        var aid = e.Properties.AutomationId.ValueOrDefault ?? ""; var name = e.Properties.Name.ValueOrDefault ?? "";
        return aid is "Chevron" or "OverflowButton" || aid == "SystemTrayIcon" && (name.Contains("隐藏", StringComparison.Ordinal) || name.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }
    internal static bool IsIcon(AutomationElement e, AutomationElement root)
    {
        if (e.Properties.ControlType.ValueOrDefault != ControlType.Button || IsOverflow(e)) return false;
        if (e.Properties.AutomationId.ValueOrDefault == "NotifyItemIcon") return true;
        if (root.Properties.ClassName.ValueOrDefault is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return true;
        var parent = e.Parent;
        for (var i = 0; parent != null && i < 8; i++, parent = parent.Parent)
        {
            if (parent.Properties.ClassName.ValueOrDefault == "TrayNotifyWnd") return true;
            if (Program.SafeElementId(parent) == Program.SafeElementId(root)) break;
        }
        return false;
    }
    internal static ToolResult List(UIA3Automation automation, string mode)
    {
        var icons = new List<object>(); var overflow = new List<object>();
        foreach (var root in automation.GetDesktop().FindAllChildren().Where(IsTrayRoot))
        {
            var windowId = Program.WindowId(root);
            foreach (var (element, _) in Program.Walk(root, 16))
            {
                try
                {
                    if (!IsOverflow(element) && !IsIcon(element, root)) continue;
                    var item = new { windowId, elementId = Program.SafeElementId(element), name = element.Properties.Name.ValueOrDefault ?? "", automationId = element.Properties.AutomationId.ValueOrDefault ?? "", role = element.Properties.ControlType.ValueOrDefault.ToString(), offscreen = element.Properties.IsOffscreen.ValueOrDefault,
                        source = root.Properties.ClassName.ValueOrDefault, bounds = element.BoundingRectangle, patterns = Program.Patterns(element) };
                    if (IsOverflow(element)) overflow.Add(item); else icons.Add(item);
                }
                catch { /* Icons may disappear when Explorer refreshes. */ }
            }
        }
        return ToolResult.Success(new { mode, icons, overflowControls = overflow, hint = "If the application icon is absent, click an observed overflow control using computer_action, then call computer_tray again. Tray icons belong to Explorer, not the application window. Use computer_tray_action on an observed icon; inspect resulting windows/menus before continuing." });
    }
    internal static ToolResult Act(AutomationElement root, string elementId, string action, string mode)
    {
        if (!IsTrayRoot(root)) return ToolResult.Failure("Target is not an observed taskbar/tray window.");
        var icon = Program.Walk(root, 16).Select(p => p.Element).FirstOrDefault(e => Program.SafeElementId(e) == elementId && IsIcon(e, root));
        if (icon == null) return ToolResult.Failure("Tray icon is stale or not in the notification area. Read computer_tray again.");
        var name = icon.Properties.Name.ValueOrDefault ?? ""; string method;
        if (action == "open")
        {
            if (icon.Patterns.Invoke.IsSupported) { icon.Patterns.Invoke.Pattern.Invoke(); method = "invoke"; }
            else if (icon.Patterns.LegacyIAccessible.IsSupported) { icon.Patterns.LegacyIAccessible.Pattern.DoDefaultAction(); method = "legacy_default_action"; }
            else return ToolResult.Failure("Tray icon lacks a semantic default action. AI may choose foreground for a physical double-click.");
        }
        else
        {
            if (mode == "BackgroundOnly") return ToolResult.Failure("Physical tray double/right click requires foreground or isolated mode. AI can choose the mode; do not silently inject input in background.");
            if (action is not ("double_click" or "right_click" or "click")) return ToolResult.Failure("Tray action must be open, click, double_click or right_click.");
            var point = icon.GetClickablePoint();
            if (GetAncestor(WindowFromPoint(point), 2) != root.Properties.NativeWindowHandle.Value) return ToolResult.Failure("Tray icon is covered or its popup closed. No input was sent.");
            // Taskbar/overflow windows need not become foreground to receive a hit-tested click.
            if (action == "double_click") icon.DoubleClick(); else if (action == "right_click") icon.RightClick(); else icon.Click();
            method = action;
        }
        return ToolResult.Success(new { mode, windowTitle = "系统托盘", targetName = name, requestedAction = action, executed = method, verificationRequired = true,
            hint = "Default action completed, not proof that a main window opened. Enumerate windows; a tray app may instead display a context menu. Read and invoke the observed Open/Settings menu entry." });
    }
}
