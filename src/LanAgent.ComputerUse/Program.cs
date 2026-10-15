using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LanAgent.Core.Tools;

namespace LanAgent.ComputerUse;

internal static class Program
{
    private static Process? _isolatedIndicator;
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);

    [MTAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        SetProcessDpiAwarenessContext(-4); // PerMonitorV2, physical UIA coordinates.
        if (args.Length == 2 && args[0] == "--overlay") return DesktopActivityOverlay.Run(args[1]);
        if (args.Length == 2 && args[0] == "--overlay-render") return DesktopActivityOverlay.Run("ForegroundAllowed", args[1]);
        if (args.Length == 3 && args[0] == "--serve") return Serve(int.Parse(args[1]), args[2]);
        ToolResult result;
        try
        {
            var buffer = new char[32001]; var count = 0; int read;
            while (count < buffer.Length && (read = Console.In.Read(buffer, count, buffer.Length - count)) > 0) count += read;
            if (count > 32000) throw new InvalidOperationException("Request exceeds 32000 characters.");
            using var doc = JsonDocument.Parse(new string(buffer, 0, count));
            result = Execute(doc.RootElement);
        }
        catch (Exception ex) { result = ToolResult.Failure(ex.GetType().Name + ": " + ex.Message); }
        Console.Write(result.ToJson()); return 0;
    }

    private static ToolResult Execute(JsonElement request)
    {
        var mode = request.GetProperty("mode").GetString();
        if (mode is not ("BackgroundOnly" or "ForegroundAllowed" or "IsolatedSession")) throw new InvalidOperationException("Invalid mode.");
        if (mode == "IsolatedSession" && request.GetProperty("originMachine").GetString() == Environment.MachineName && request.GetProperty("originSession").GetInt32() == Process.GetCurrentProcess().SessionId)
            throw new InvalidOperationException("Isolated mode refuses to operate in the caller's desktop session.");
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0) throw new InvalidOperationException("Worker requires an interactive Windows desktop session; Session 0 is unsupported.");
        var operation = request.GetProperty("operation").GetString();
        var args = request.GetProperty("arguments");
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        if (operation == "tray") return SystemTray.List(automation, mode!);
        if (operation == "windows")
        {
            var windows = new List<object>();
            foreach (var e in desktop.FindAllChildren().Take(100))
            {
                try { if (e.Name != DesktopActivityOverlay.WindowTitle) windows.Add(new { windowId = WindowId(e), name = Clip(e.Name), processId = e.Properties.ProcessId.Value, bounds = e.BoundingRectangle }); }
                catch { /* Windows can disappear during enumeration. */ }
            }
            return ToolResult.Success(windows);
        }
        var windowId = args.GetProperty("windowId").GetString();
        if (windowId == null || !long.TryParse(windowId.Split(':')[0], out var handle)) throw new InvalidOperationException("Invalid observed window ID.");
        var window = automation.FromHandle((nint)handle);
        if (SafeWindowId(window) != windowId) throw new InvalidOperationException("Window no longer exists. Read computer_windows again.");
        if (operation == "tray_action") return SystemTray.Act(window, args.GetProperty("elementId").GetString()!, args.GetProperty("action").GetString()!, mode!);
        if (operation == "screenshot") return WindowScreenshot.Capture((nint)handle, window.Properties.Name.ValueOrDefault ?? "", mode!);
        if (operation == "diagnose")
        {
            string Probe(Func<object?> read) { try { return JsonSerializer.Serialize(read()); } catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; } }
            object Inspect(AutomationElement e) => new { name = Probe(() => e.Properties.Name.ValueOrDefault), role = Probe(() => e.Properties.ControlType.ValueOrDefault.ToString()), automationId = Probe(() => e.AutomationId), runtimeId = Probe(() => e.Properties.RuntimeId.Value), fingerprint = Probe(() => ElementId(e)), framework = Probe(() => e.Properties.FrameworkId.ValueOrDefault), className = Probe(() => e.ClassName) };
            object Traverse(bool raw)
            {
                var walker = raw ? automation.TreeWalkerFactory.GetRawViewWalker() : automation.TreeWalkerFactory.GetControlViewWalker();
                var nodes = new List<object>(); var stack = new Stack<(AutomationElement Element, int Depth)>(); stack.Push((window, 0));
                while (stack.Count > 0 && nodes.Count < 80)
                {
                    var current = stack.Pop(); nodes.Add(new { depth = current.Depth, properties = Inspect(current.Element) });
                    try { var sibling = current.Depth > 0 ? walker.GetNextSibling(current.Element) : null; if (sibling != null) stack.Push((sibling, current.Depth)); var child = current.Depth < 12 ? walker.GetFirstChild(current.Element) : null; if (child != null) stack.Push((child, current.Depth + 1)); }
                    catch (Exception ex) { nodes.Add(new { error = ex.Message }); }
                }
                return nodes;
            }
            return ToolResult.Success(new { windowId, root = Inspect(window), raw = Traverse(true), control = Traverse(false) });
        }
        if (operation is "find" or "wait")
        {
            var timer = Stopwatch.StartNew();
            var timeout = operation == "wait" ? args.GetProperty("timeoutMs").GetInt32() : 0;
            if (timeout is < 0 or > 10000) throw new InvalidOperationException("Invalid wait timeout.");
            do
            {
                var matches = Walk(window, 16).Where(pair => Matches(pair.Element, args)).Take(21).Select(pair => pair.Element).ToArray();
                if (operation == "find") return ToolResult.Success(new { windowId, windowTitle = Clip(window.Name), mode, elements = matches.Take(20).Select(e => Describe(e, 0)).ToArray() }, matches.Length > 20);
                var condition = args.GetProperty("condition").GetString()!;
                if (condition is not ("exists" or "enabled" or "selected" or "on" or "off" or "expanded" or "collapsed")) throw new InvalidOperationException("Unknown wait condition.");
                // A selector must identify one control before interpreting its value/state.
                if (matches.Length == 1 && Satisfies(matches[0], condition, args.GetProperty("value").GetString()!))
                    return ToolResult.Success(new { windowId, windowTitle = Clip(window.Name), mode, condition, elapsedMs = timer.ElapsedMilliseconds, element = Describe(matches[0], 0), verified = true });
                if (timer.ElapsedMilliseconds >= timeout) break;
                Thread.Sleep(100);
            } while (true);
            return ToolResult.Failure("Expected unique UI control/state was not observed before the deadline. Inspect the current tree; do not repeat the previous action blindly.");
        }
        if (operation == "tree")
        {
            var max = args.GetProperty("maxElements").GetInt32(); var depth = args.GetProperty("maxDepth").GetInt32();
            var offset = args.TryGetProperty("offset", out var start) ? start.GetInt32() : 0;
            if (max is < 1 or > 200 || depth is < 1 or > 16 || offset is < 0 or > 1999) throw new InvalidOperationException("Invalid tree limits.");
            var elements = new List<object>(); var budget = 0; var truncated = false;
            var examined = 0;
            foreach (var (element, level) in Walk(window, depth).Skip(offset))
            {
                if (elements.Count >= max || budget >= 45000) { truncated = true; break; }
                try
                {
                    var node = Describe(element, level);
                    budget += JsonSerializer.Serialize(node, ToolResult.JsonOptions).Length; elements.Add(node);
                }
                catch { truncated = true; }
                examined++;
            }
            truncated |= offset + examined >= 2000;
            return ToolResult.Success(new { windowId, windowTitle = Clip(window.Name), mode, elements, nextOffset = truncated && offset + examined < 2000 ? (int?)(offset + examined) : null, scanLimit = 2000 }, truncated);
        }
        if (operation is "keys" or "focus")
        {
            if (mode == "BackgroundOnly") throw new InvalidOperationException("Background mode prohibits input and focus.");
            VirtualKeyShort[] keys = [];
            if (operation == "keys") keys = ParseKeys(args.GetProperty("keys").GetString()!);
            window.SetForeground();
            if (GetForegroundWindow() != window.Properties.NativeWindowHandle.Value) throw new InvalidOperationException("Target window did not become foreground; input was not sent.");
            if (operation == "keys") NativeInput.Chord(keys);
            return ToolResult.Success(new { executed = operation, mode, windowTitle = Clip(window.Name), keys = operation == "keys" ? args.GetProperty("keys").GetString() : null, verificationRequired = true });
        }
        if (operation != "action") throw new InvalidOperationException("Unknown operation.");
        var id = args.GetProperty("elementId").GetString();
        var target = Walk(window, 16).Select(pair => pair.Element).FirstOrDefault(e => SafeElementId(e) == id)
            ?? throw new InvalidOperationException("Element is stale or outside the bounded tree. Read computer_tree again.");
        if (!target.IsEnabled) throw new InvalidOperationException("Element is disabled.");
        if (target.Properties.IsPassword.ValueOrDefault) throw new InvalidOperationException("Password controls are not supported.");
        var action = args.GetProperty("action").GetString();
        var targetName = Clip(target.Name); var targetRole = target.ControlType.ToString(); var windowTitle = Clip(window.Name);
        var requestedAction = action;
        if (action == "click" && mode == "BackgroundOnly")
            action = target.Patterns.Invoke.IsSupported ? "invoke" : target.Patterns.SelectionItem.IsSupported ? "select" : target.Patterns.Toggle.IsSupported ? "toggle" : target.Patterns.ExpandCollapse.IsSupported ? "expand" : target.Patterns.LegacyIAccessible.IsSupported && !string.IsNullOrEmpty(target.Patterns.LegacyIAccessible.Pattern.DefaultAction.ValueOrDefault) ? "legacy_default_action" : "unsupported";
        if (action == "type_text" && target.Patterns.Value.IsSupported) action = "set_value";
        try
        {
        switch (action)
        {
            case "invoke" when target.Patterns.Invoke.IsSupported: target.Patterns.Invoke.Pattern.Invoke(); break;
            case "legacy_default_action" when target.Patterns.LegacyIAccessible.IsSupported: target.Patterns.LegacyIAccessible.Pattern.DoDefaultAction(); break;
            case "set_value" when target.Patterns.Value.IsSupported:
                var text = args.GetProperty("text").GetString()!;
                if (text.Length > 8000) throw new InvalidOperationException("Text exceeds 8000 characters.");
                if (target.Patterns.Value.Pattern.IsReadOnly.Value) throw new InvalidOperationException("Control is read-only.");
                target.Patterns.Value.Pattern.SetValue(text); break;
            case "select" when target.Patterns.SelectionItem.IsSupported: target.Patterns.SelectionItem.Pattern.Select(); break;
            case "toggle" when target.Patterns.Toggle.IsSupported: target.Patterns.Toggle.Pattern.Toggle(); break;
            case "expand" when target.Patterns.ExpandCollapse.IsSupported: target.Patterns.ExpandCollapse.Pattern.Expand(); break;
            case "collapse" when target.Patterns.ExpandCollapse.IsSupported: target.Patterns.ExpandCollapse.Pattern.Collapse(); break;
            case "scroll_up" when target.Patterns.Scroll.IsSupported: target.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement); break;
            case "scroll_down" when target.Patterns.Scroll.IsSupported: target.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); break;
            case "click" or "double_click" or "right_click":
                if (mode == "BackgroundOnly") throw new InvalidOperationException("Background mode prohibits physical mouse input. Use supported semantic patterns or ask the user to change mode.");
                window.SetForeground();
                if (GetForegroundWindow() != window.Properties.NativeWindowHandle.Value) throw new InvalidOperationException("Target window did not become foreground; click was not sent.");
                var point = target.GetClickablePoint();
                if (GetAncestor(WindowFromPoint(point), 2) != window.Properties.NativeWindowHandle.Value) throw new InvalidOperationException("Target is covered by another window. No physical click was sent.");
                if (action == "click") target.Click(); else if (action == "double_click") target.DoubleClick(); else target.RightClick(); break;
            case "type_text":
                if (mode == "BackgroundOnly") throw new InvalidOperationException("Control lacks a writable Value pattern. Full background keyboard input requires an isolated Windows environment.");
                var input = args.GetProperty("text").GetString()!;
                if (input.Length > 8000) throw new InvalidOperationException("Text exceeds 8000 characters.");
                window.SetForeground(); target.Focus();
                if (GetForegroundWindow() != window.Properties.NativeWindowHandle.Value || !target.Properties.HasKeyboardFocus.ValueOrDefault) throw new InvalidOperationException("Target control did not gain focus; text was not sent.");
                NativeInput.Text(input); break;
            default: throw new InvalidOperationException("Unsupported action or UIA pattern. No automatic foreground fallback is allowed.");
        }
        // Return observed data, not a blanket claim that the task succeeded.
        object? observed = null;
        try { if (target.IsAvailable) observed = Describe(target, 0); } catch { }
        return ToolResult.Success(new { executed = action, requestedAction, mode, windowTitle, targetName, targetRole, elementId = id,
            textPreview = requestedAction is "type_text" or "set_value" ? Clip(args.GetProperty("text").GetString(), 100) : null,
            observed, verificationRequired = true });
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(ex.Message) with { Data = new { mode, windowTitle, targetName, targetRole, requestedAction, verificationRequired = true } };
        }
    }

    internal static IEnumerable<(AutomationElement Element, int Depth)> Walk(AutomationElement root, int maxDepth)
    {
        // Iterate siblings individually: FindAllChildren can materialize thousands of rows
        // before a caller's Take/budget ever runs. Protect against provider cycles as well.
        var walker = root.Automation.TreeWalkerFactory.GetControlViewWalker();
        var stack = new Stack<(AutomationElement Element, int Depth)>(); stack.Push((root, 0));
        var seen = new HashSet<string>(); var visited = 0;
        while (stack.Count > 0 && visited++ < 2000)
        {
            var item = stack.Pop();
            var runtimeId = SafeElementId(item.Element);
            if (runtimeId != null && !seen.Add(runtimeId)) continue;
            AutomationElement? sibling = null; AutomationElement? child = null;
            try { if (item.Depth > 0) sibling = walker.GetNextSibling(item.Element); if (item.Depth < maxDepth) child = walker.GetFirstChild(item.Element); } catch { }
            if (sibling != null) stack.Push((sibling, item.Depth));
            if (child != null) stack.Push((child, item.Depth + 1));
            yield return item;
        }
    }
    private static object Describe(AutomationElement e, int depth)
    {
        var password = e.Properties.IsPassword.ValueOrDefault;
        return new { elementId = SafeElementId(e), depth, role = e.Properties.ControlType.ValueOrDefault.ToString(), name = password ? "[password]" : Clip(e.Properties.Name.ValueOrDefault), automationId = Clip(e.Properties.AutomationId.ValueOrDefault), enabled = e.Properties.IsEnabled.ValueOrDefault, offscreen = e.Properties.IsOffscreen.ValueOrDefault, password, bounds = e.BoundingRectangle,
            patterns = Patterns(e), value = password ? null : ReadValue(e),
            toggleState = e.Patterns.Toggle.IsSupported ? e.Patterns.Toggle.Pattern.ToggleState.Value.ToString() : null,
            selected = e.Patterns.SelectionItem.IsSupported ? (bool?)e.Patterns.SelectionItem.Pattern.IsSelected.Value : null,
            expandState = e.Patterns.ExpandCollapse.IsSupported ? e.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString() : null,
            verticalScrollPercent = e.Patterns.Scroll.IsSupported ? (double?)e.Patterns.Scroll.Pattern.VerticalScrollPercent.Value : null };
    }
    private static string? ReadValue(AutomationElement e) => e.Patterns.Value.IsSupported ? Clip(e.Patterns.Value.Pattern.Value.ValueOrDefault, 500)
        : e.Patterns.Text.IsSupported ? Clip(e.Patterns.Text.Pattern.DocumentRange.GetText(500), 500) : null;
    private static bool Matches(AutomationElement e, JsonElement args)
    {
        try { return (!args.TryGetProperty("name", out var name) || name.GetString() == "" || e.Name == name.GetString()) &&
            (!args.TryGetProperty("automationId", out var aid) || aid.GetString() == "" || e.Properties.AutomationId.ValueOrDefault == aid.GetString()) &&
            (!args.TryGetProperty("role", out var role) || role.GetString() == "" || e.ControlType.ToString().Equals(role.GetString(), StringComparison.OrdinalIgnoreCase)); } catch { return false; }
    }
    private static bool Satisfies(AutomationElement e, string condition, string value)
    {
        if (e.Properties.IsPassword.ValueOrDefault) return false;
        var actual = value == "" ? null : e.Patterns.Value.IsSupported ? e.Patterns.Value.Pattern.Value.ValueOrDefault : e.Patterns.Text.IsSupported ? e.Patterns.Text.Pattern.DocumentRange.GetText(Math.Min(8001, value.Length + 1)) : null;
        return (value == "" || actual == value) && condition switch {
            "exists" => true, "enabled" => e.IsEnabled,
            "selected" => e.Patterns.SelectionItem.IsSupported && e.Patterns.SelectionItem.Pattern.IsSelected.Value,
            "on" => e.Patterns.Toggle.IsSupported && e.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On,
            "off" => e.Patterns.Toggle.IsSupported && e.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off,
            "expanded" => e.Patterns.ExpandCollapse.IsSupported && e.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == ExpandCollapseState.Expanded,
            "collapsed" => e.Patterns.ExpandCollapse.IsSupported && e.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == ExpandCollapseState.Collapsed,
            _ => false };
    }
    internal static string WindowId(AutomationElement e)
    { using var process = Process.GetProcessById(e.Properties.ProcessId.Value); return $"{e.Properties.NativeWindowHandle.Value}:{process.Id}:{process.StartTime.ToUniversalTime().Ticks}"; }
    private static string? SafeWindowId(AutomationElement e) { try { return WindowId(e); } catch { return null; } }
    private static string ElementId(AutomationElement e) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { runtime = e.Properties.RuntimeId.Value, role = e.Properties.ControlType.ValueOrDefault.ToString(), automationId = e.Properties.AutomationId.ValueOrDefault, name = e.Properties.Name.ValueOrDefault }))));
    internal static string? SafeElementId(AutomationElement e) { try { return ElementId(e); } catch { return null; } }
    private static string Clip(string? s, int limit = 120) => ToolResult.Clip(s, limit);
    internal static string[] Patterns(AutomationElement e) => new[] {
        e.Patterns.Invoke.IsSupported ? "Invoke" : null, e.Patterns.Value.IsSupported ? "Value" : null,
        e.Patterns.SelectionItem.IsSupported ? "SelectionItem" : null, e.Patterns.Toggle.IsSupported ? "Toggle" : null,
        e.Patterns.ExpandCollapse.IsSupported ? "ExpandCollapse" : null, e.Patterns.Scroll.IsSupported ? "Scroll" : null, e.Patterns.Text.IsSupported ? "Text" : null, e.Patterns.LegacyIAccessible.IsSupported ? "LegacyIAccessible" : null
    }.OfType<string>().ToArray();
    private static VirtualKeyShort[] ParseKeys(string chord)
    {
        var parts = chord.ToUpperInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 5) throw new InvalidOperationException("Invalid chord.");
        return parts.Select(p => p switch { "CTRL" => VirtualKeyShort.CONTROL, "ALT" => VirtualKeyShort.ALT, "WIN" => VirtualKeyShort.LWIN, "ENTER" => VirtualKeyShort.RETURN, "ESC" => VirtualKeyShort.ESCAPE, "DEL" => VirtualKeyShort.DELETE, "BACKSPACE" => VirtualKeyShort.BACK,
            _ => Enum.TryParse<VirtualKeyShort>(p.Length == 1 && char.IsLetterOrDigit(p[0]) ? "KEY_" + p : p, out var key) && Enum.IsDefined(key) ? key : throw new InvalidOperationException("Unknown key: " + p) }).ToArray();
    }

    // Launch this listener interactively in a separate VM/session. Reach it through a secure tunnel.
    private static int Serve(int port, string tokenFile)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("Invalid port.");
        var secret = File.ReadAllText(tokenFile).Trim();
        if (secret.Length < 32) throw new ArgumentException("Token requires at least 32 characters.");
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        while (true)
        {
            var context = listener.GetContext();
            try
            {
                var provided = context.Request.Headers["Authorization"] ?? "";
                if (context.Request.HttpMethod != "POST" || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes("Bearer " + secret)))
                { context.Response.StatusCode = 401; context.Response.Close(); continue; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var body = LanAgent.Core.Runtime.BoundedProcess.DrainAsync(reader, 32000, deadline.Token).GetAwaiter().GetResult();
                if (body.Truncated) throw new InvalidOperationException("Request too large.");
                var input = body.Text;
                using var doc = JsonDocument.Parse(input);
                if (doc.RootElement.GetProperty("mode").GetString() != "IsolatedSession") throw new InvalidOperationException("Listener accepts isolated mode only.");
                using var isolatedIndicator = doc.RootElement.GetProperty("operation").GetString() is "action" or "tray_action" or "keys" or "focus"
                    ? ShowIsolatedIndicator("IsolatedSession") : null;
                var info = LanAgent.Core.Runtime.BoundedProcess.StartInfo(Environment.ProcessPath!, []); info.RedirectStandardInput = true; info.StandardInputEncoding = new UTF8Encoding(false);
                using var process = new Process { StartInfo = info }; process.Start();
                try
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                    process.StandardInput.Write(input); process.StandardInput.Close();
                    if (!process.WaitForExit(15000)) { LanAgent.Core.Runtime.BoundedProcess.Kill(process); throw new TimeoutException("Isolated UIA Worker exceeded 15 seconds."); }
                    var data = output.GetAwaiter().GetResult(); _ = errors.GetAwaiter().GetResult();
                    WriteResponse(context, data);
                }
                finally { LanAgent.Core.Runtime.BoundedProcess.Kill(process); }
            }
            catch (Exception ex) { try { WriteResponse(context, ToolResult.Failure(ex.Message).ToJson()); } catch { context.Response.Abort(); } }
        }
    }
    private static void WriteResponse(HttpListenerContext context, string json)
    { var bytes = Encoding.UTF8.GetBytes(json); context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes); context.Response.Close(); }

    private static IDisposable ShowIsolatedIndicator(string mode)
    {
        if (_isolatedIndicator != null)
        {
            try
            {
                if (!_isolatedIndicator.HasExited)
                {
                    _isolatedIndicator.StandardInput.WriteLine("active\t" + mode);
                    _isolatedIndicator.StandardInput.Flush();
                    using var activeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    if (_isolatedIndicator.StandardOutput.ReadLineAsync(activeDeadline.Token).AsTask().GetAwaiter().GetResult() == "active")
                        return new IndicatorLease(_isolatedIndicator);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
            LanAgent.Core.Runtime.BoundedProcess.Kill(_isolatedIndicator);
            _isolatedIndicator.Dispose(); _isolatedIndicator = null;
        }
        var info = LanAgent.Core.Runtime.BoundedProcess.StartInfo(Environment.ProcessPath!, ["--overlay", mode]);
        info.RedirectStandardInput = true;
        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() != "ready")
                throw new InvalidOperationException("Computer Use indicator could not be displayed.");
            _isolatedIndicator = process;
            return new IndicatorLease(process);
        }
        catch { LanAgent.Core.Runtime.BoundedProcess.Kill(process); process.Dispose(); throw; }
    }
    private sealed class IndicatorLease(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { if (!process.HasExited) { process.StandardInput.WriteLine("idle"); process.StandardInput.Flush(); } }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
    }
}
