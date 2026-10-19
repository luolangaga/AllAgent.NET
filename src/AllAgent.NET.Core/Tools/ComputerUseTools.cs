using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

public partial class ComputerUseTools(ComputerUseState state) : IAgentToolSet, IAgentToolPreflight
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public ToolResult? ValidateInvocation(string name, JsonElement arguments)
    {
        var current = state.Snapshot();
        if (name == "computer_status") return null;
        if (!OperatingSystem.IsWindows()) return ToolResult.Failure("Computer Use requires Windows.");
        if (name == "computer_mode") return null;
        if (current.Mode == ComputerInteractionMode.BackgroundOnly && name == "computer_tray_action" && ToolArguments.Get<string>(arguments, "action", "open") != "open")
            return ToolResult.Failure("Physical tray input requires foreground/isolated mode; AI can choose it with computer_mode. Background supports the semantic open action.");
        if (current.Mode == ComputerInteractionMode.BackgroundOnly && name is "computer_keys" or "computer_focus")
            return ToolResult.Failure("Background UIA does not use global focus or keys. Use set_value/invoke, or computer_mode to choose isolated for uninterrupted input. Use foreground only if consistent with the user's requested interaction.");
        if (current.Mode == ComputerInteractionMode.BackgroundOnly && name == "computer_action" && ToolArguments.Get<string>(arguments, "action") is "double_click" or "right_click")
            return ToolResult.Failure("Background mode prohibits physical clicks; use a supported UIA pattern. No automatic foreground fallback is allowed.");
        if (current.Mode == ComputerInteractionMode.IsolatedSession && (state.IsolatedEndpoint == null || state.IsolatedTokenFile == null))
            return ToolResult.Failure("Isolated mode requires an independent Windows session Worker, endpoint and token file. No local fallback is allowed.");
        if (current.Mode != ComputerInteractionMode.IsolatedSession && !File.Exists(state.WorkerPath))
            return ToolResult.Failure("Computer Use Worker is missing: " + state.WorkerPath);
        return null;
    }

    [AgentTool("computer_status")]
    [Description("Read the current interaction mode and isolated configuration. Computer Use is always available; per-call approvals still apply.")]
    public Task<ToolResult> StatusAsync(CancellationToken ct = default)
    {
        var current = state.Snapshot();
        return Task.FromResult(ToolResult.Success(new { mode = current.Mode.ToString(), isolatedConfigured = state.IsolatedEndpoint != null && state.IsolatedTokenFile != null, workerInstalled = File.Exists(state.WorkerPath) }));
    }

    [AgentTool("computer_mode", Risk = ToolRisk.Execute)]
    [Description("Choose background, foreground or isolated for the authorized task. Software testing starts in background (UIA). For full keyboard/mouse without interrupting the user choose isolated. Foreground uses the user's input devices. Mode selection changes how input is delivered, never whether Computer Use is available. Read the computer-use skill first.")]
    public async Task<ToolResult> ModeAsync([Description("background, foreground or isolated")] string mode, [Description("Why this task needs the mode")] string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var selected = mode switch { "background" => ComputerInteractionMode.BackgroundOnly, "foreground" => ComputerInteractionMode.ForegroundAllowed, "isolated" => ComputerInteractionMode.IsolatedSession, _ => (ComputerInteractionMode?)null };
        if (selected == null || string.IsNullOrWhiteSpace(reason)) return ToolResult.Failure("Provide a valid mode and reason.");
        if (selected == ComputerInteractionMode.IsolatedSession && (state.IsolatedEndpoint == null || state.IsolatedTokenFile == null))
            return ToolResult.Failure("Independent Windows environment is not configured. Full input without disturbing the user requires that environment; do not silently switch to foreground.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Snapshot().Token);
        await state.Gate.WaitAsync(linked.Token);
        try
        {
            var previous = state.Snapshot().Mode;
            return state.TrySelectMode(selected.Value)
                ? ToolResult.Success(new { previousMode = previous.ToString(), mode = selected.ToString(), reason = ToolResult.Clip(reason, 500) })
                : ToolResult.Failure("Computer Use is disabled. Only the user can enable it with /computer on.");
        }
        finally { state.Gate.Release(); }
    }

    [AgentTool("computer_windows")]
    [Description("List Windows application windows with windowId and processId. Software testing defaults to background UIA operations.")]
    public Task<ToolResult> WindowsAsync(CancellationToken ct = default) => RunAsync("windows", new { }, ct);

    [AgentTool("computer_tray")]
    [Description("List Explorer system-tray icons and hidden-icon overflow controls. Tray-only apps may not expose their main window until their icon is activated. If the icon is absent, click an observed overflow control with computer_action, then list again. Does not expand/click anything by itself.")]
    public Task<ToolResult> TrayAsync(CancellationToken ct = default) => RunAsync("tray", new { }, ct);

    [AgentTool("computer_tray_action", Risk = ToolRisk.Execute)]
    [Description("Activate an observed system-tray icon. open uses its semantic default action (background compatible); click/double_click/right_click require foreground or isolated. Validate returned windows/menus afterwards: open may display a tray menu instead of a main window. Taskbar icons belong to Explorer. Use IDs from computer_tray; never relaunch the EXE blindly.")]
    public Task<ToolResult> TrayActionAsync([Description("Tray windowId from computer_tray")] string windowId, [Description("Tray elementId from computer_tray")] string elementId, [Description("open, click, double_click or right_click")] string action = "open", CancellationToken ct = default) => RunAsync("tray_action", new { windowId, elementId, action }, ct);

    [AgentTool("computer_screenshot")]
    [Description("Capture an observed window without activating it. The host forwards the actual image to the model after execute_code finishes; print the metadata. Useful when UIA is empty or for visual verification. PrintWindow may yield blank frames for GPU/protected windows; do not assume a capture is useful until inspected.")]
    public Task<ToolResult> ScreenshotAsync([Description("Observed windowId")] string windowId, CancellationToken ct = default) => RunAsync("screenshot", new { windowId }, ct);

    [AgentTool("computer_tree")]
    [Description("Read a bounded UIA control tree of a previously observed window. Returns temporary elementId, role, name, automationId, patterns and bounds. Read before acting; do not invent IDs. Password values are not returned.")]
    public Task<ToolResult> TreeAsync([Description("Observed windowId")] string windowId, [Description("Maximum controls, 1..200")] int maxElements = 100, [Description("Maximum tree depth, 1..16")] int maxDepth = 12, [Description("Page offset from nextOffset in the previous tree result")] int offset = 0, CancellationToken ct = default) =>
        maxElements is < 1 or > 200 || maxDepth is < 1 or > 16 || offset is < 0 or > 1999 ? Task.FromResult(ToolResult.Failure("maxElements must be 1..200; maxDepth 1..16; offset 0..1999.")) : RunAsync("tree", new { windowId, maxElements, maxDepth, offset }, ct);

    [AgentTool("computer_find")]
    [Description("Find controls within one observed window by exact name, automationId and/or role; empty fields are ignored. Returns up to 20 matches with IDs and patterns. Do not act on ambiguous matches. This reads only and does not focus windows.")]
    public Task<ToolResult> FindAsync([Description("Observed windowId")] string windowId, [Description("Exact accessible name")] string name = "", [Description("Exact automation ID")] string automationId = "", [Description("UIA role, e.g. Edit or Button")] string role = "", CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(name + automationId + role) ? Task.FromResult(ToolResult.Failure("Provide at least one selector.")) : RunAsync("find", new { windowId, name, automationId, role }, ct);

    [AgentTool("computer_wait")]
    [Description("Wait for a control matching exact selectors and optionally exact value/state. Polls read-only observations in the Worker; never repeats actions. States: exists, enabled, selected, on, off, expanded, collapsed. For label text use name. Timeout returns failure with no action retry.")]
    public Task<ToolResult> WaitAsync([Description("Observed windowId")] string windowId, [Description("Exact accessible name")] string name = "", [Description("Exact automation ID")] string automationId = "", [Description("UIA role")] string role = "", [Description("exists, enabled, selected, on, off, expanded or collapsed")] string condition = "exists", [Description("Exact expected Value text; empty ignores value")] string value = "", [Description("Timeout in milliseconds, 100..10000")] int timeoutMs = 3000, CancellationToken ct = default) =>
        timeoutMs is < 100 or > 10000 || string.IsNullOrWhiteSpace(name + automationId + role) ? Task.FromResult(ToolResult.Failure("Provide a selector and timeoutMs 100..10000.")) : RunAsync("wait", new { windowId, name, automationId, role, condition, value, timeoutMs }, ct);

    [AgentTool("computer_action", Risk = ToolRisk.Execute)]
    [Description("Act on an observed control. click uses Invoke/SelectionItem/Toggle/ExpandCollapse in background; type_text uses Value.SetValue without focus in background. Also supports invoke, set_value, select, toggle, expand, collapse, scroll_up/down, double_click, right_click. Foreground/isolated allow physical input when necessary. Unsupported background controls return a reason; AI may choose isolated for full input. Verify using computer_wait/tree after actions.")]
    public Task<ToolResult> ActionAsync([Description("Observed windowId")] string windowId, [Description("Observed temporary elementId")] string elementId, [Description("Semantic operation or explicit physical click")] string action, [Description("Text for set_value")] string text = "", CancellationToken ct = default) =>
        text.Length > 8000 ? Task.FromResult(ToolResult.Failure("Text exceeds 8000 characters.")) : RunAsync("action", new { windowId, elementId, action, text }, ct);

    [AgentTool("computer_focus", Risk = ToolRisk.Execute)]
    [Description("Activate an observed window. Forbidden in background mode.")]
    public Task<ToolResult> FocusAsync([Description("Observed windowId")] string windowId, CancellationToken ct = default) => RunAsync("focus", new { windowId }, ct);

    [AgentTool("computer_keys", Risk = ToolRisk.Execute)]
    [Description("Activate an observed window and send a key chord, e.g. CTRL+S, ALT+F4, RETURN. Forbidden in background mode. Use only after inspecting the target window.")]
    public Task<ToolResult> KeysAsync([Description("Observed windowId")] string windowId, [Description("Chord, e.g. CTRL+S or RETURN")] string keys, CancellationToken ct = default) => RunAsync("keys", new { windowId, keys }, ct);

    private async Task<ToolResult> RunAsync(string operation, object arguments, CancellationToken ct)
    {
        var snapshot = state.Snapshot();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, snapshot.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(20));
        var token = linked.Token;
        var outputLimit = operation == "screenshot" ? 6 * 1024 * 1024 : 64000;
        try
        {
            await state.Gate.WaitAsync(token);
            try
            {
                var error = ValidateInvocation("computer_" + operation, JsonSerializer.SerializeToElement(arguments));
                if (error != null) return error;
                token.ThrowIfCancellationRequested();
                var request = JsonSerializer.Serialize(new { operation, arguments, mode = snapshot.Mode.ToString(), originMachine = Environment.MachineName, originSession = Process.GetCurrentProcess().SessionId }, ToolResult.JsonOptions);
                if (snapshot.Mode == ComputerInteractionMode.IsolatedSession)
                {
                    var endpoint = state.IsolatedEndpoint!;
                    if (!endpoint.IsLoopback || endpoint.Scheme != "http") return ToolResult.Failure("Use a loopback HTTP endpoint via a tunnel to the isolated Windows Worker.");
                    var secret = (await File.ReadAllTextAsync(state.IsolatedTokenFile!, token)).Trim();
                    using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(request, Encoding.UTF8, "application/json") };
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var reader = new StreamReader(stream);
                    var result = await BoundedProcess.DrainAsync(reader, outputLimit, token);
                    if (result.Truncated) return ToolResult.Failure("Worker response exceeded its output budget.");
                    return ParseWorkerResponse(result.Text);
                }
                var info = BoundedProcess.StartInfo(state.WorkerPath, []);
                using var indicator = operation is "action" or "tray_action" or "keys" or "focus"
                    ? await state.Activity.BeginOperationAsync(state.WorkerPath, snapshot.Mode, token)
                    : null;
                token.ThrowIfCancellationRequested();
                info.RedirectStandardInput = true; info.StandardInputEncoding = new UTF8Encoding(false);
                using var process = new Process { StartInfo = info };
                process.Start();
                using var registration = token.Register(() => BoundedProcess.Kill(process));
                var stdout = BoundedProcess.DrainAsync(process.StandardOutput, outputLimit, token);
                var stderr = BoundedProcess.DrainAsync(process.StandardError, 2000, token);
                await process.StandardInput.WriteAsync(request.AsMemory(), token); process.StandardInput.Close();
                await process.WaitForExitAsync(token);
                var output = await stdout; var errors = await stderr;
                if (process.ExitCode != 0 || output.Truncated) return ToolResult.Failure("Computer Worker failed: " + errors.Text);
                return ParseWorkerResponse(output.Text);
            }
            finally { state.Gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ToolResult.Failure("Computer operation stopped because the mode changed, it was disabled, or the 20-second deadline elapsed. Inspect state before repeating actions."); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return ToolResult.Failure(ex.Message); }
    }
    private static ToolResult ParseWorkerResponse(string text)
    {
        var result = JsonSerializer.Deserialize<ToolResult>(text, ToolResult.JsonOptions) ?? ToolResult.Failure("Invalid Worker response.");
        if (!result.Ok) return result;
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("imageBase64", out var image)) return result;
        var attachment = AllAgent.NET.Core.Models.ImageAttachment.FromBytes("computer-window.png", Convert.FromBase64String(image.GetString()!));
        var fields = data.EnumerateObject().Where(p => p.Name != "imageBase64").ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        fields["imageAttached"] = true;
        return result with { Data = fields, Images = [attachment] };
    }
}
