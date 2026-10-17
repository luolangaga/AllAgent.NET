using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Browser;

/// <summary>Shared C# browser engine for WebView2 and external CDP browsers.</summary>
public sealed partial class BrowserAutomationSession(IBrowserConnection connection) : IAsyncDisposable
{
    private string? _snapshotObject;
    private string? _snapshotTab;
    private string _dom = "";
    private JsonElement _page;
    private HashSet<string> _tabs = [];

    private const string ObserveScript = """
        (() => {
          const nodes = [], lines = [], seen = new Set(); let visited = 0, crossOriginFrames = 0;
          function walk(root) {
            const walker=document.createTreeWalker(root,NodeFilter.SHOW_ELEMENT);let e;
            while(e=walker.nextNode()) {
              if (++visited > 30000 || nodes.length >= 2000) return;
              if (seen.has(e)) continue; seen.add(e);
              if (e.shadowRoot) walk(e.shadowRoot);
              if (e.tagName === 'IFRAME') {
                try { if (e.contentDocument) walk(e.contentDocument); else crossOriginFrames++; } catch { crossOriginFrames++; }
              }
              if (!e.matches('a,button,input,textarea,select,summary,[role],[contenteditable="true"],[tabindex],label')) continue;
              const r = e.getBoundingClientRect(), style = e.ownerDocument.defaultView.getComputedStyle(e);
              if (!r.width || !r.height || style.visibility === 'hidden' || style.display === 'none') continue;
              const label = (e.getAttribute('aria-label') || e.innerText || e.getAttribute('placeholder') || e.getAttribute('title') || '').trim().replace(/\s+/g,' ').slice(0,180);
              const value = e.type === 'password' ? '[redacted]' : String(e.value ?? '').slice(0,120);
              const i = nodes.push(e) - 1;
              lines.push(`[${i}] <${e.tagName.toLowerCase()} role="${e.getAttribute('role') || ''}" type="${e.type || ''}"> ${label} ${value} ${e.disabled ? '[disabled]' : ''}`);
            }
          }
          walk(document);
          return { nodes, info: {url:location.href,title:document.title,crossOriginFrames,
            bounded:visited>30000 || nodes.length>=2000,
            dom:lines.join('\n')+'\n\nPage text:\n'+(document.body?.innerText || '').slice(0,60000)} };
        })()
        """;

    private async Task<JsonElement> EvaluateAsync(string script, CancellationToken ct, bool byValue = true)
    {
        var result = await connection.SendAsync("Runtime.evaluate", new { expression = script, returnByValue = byValue, awaitPromise = true, objectGroup = "allagent" }, ct);
        return ReadEvaluation(result, byValue);
    }
    private static JsonElement ReadEvaluation(JsonElement result, bool byValue)
    {
        if (result.TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException("Browser script failed: " + error);
        var value = result.GetProperty("result");
        return byValue ? value.TryGetProperty("value", out var json) ? json.Clone() : JsonSerializer.SerializeToElement<object?>(null) : value.Clone();
    }
    private async Task<JsonElement> OnSnapshotAsync(string function, object[] arguments, CancellationToken ct)
    {
        if (_snapshotObject == null || _snapshotTab != connection.ActiveTabId) throw new InvalidOperationException("Read fresh browser_state before using element indices.");
        var result = await connection.SendAsync("Runtime.callFunctionOn", new
        { objectId = _snapshotObject, functionDeclaration = function, arguments = arguments.Select(value => new { value }).ToArray(), returnByValue = true, awaitPromise = true }, ct);
        return ReadEvaluation(result, true);
    }
    private async Task InvalidateAsync(CancellationToken ct)
    {
        _snapshotObject = null;
        if (connection.Dialog != null) return;
        try { await connection.SendAsync("Runtime.releaseObjectGroup", new { objectGroup = "allagent" }, ct); }
        catch (InvalidOperationException) { /* Navigation destroys previous contexts. */ }
    }
    private async Task<ToolResult> ExecuteCoreAsync(string operation, JsonElement args, CancellationToken ct)
    {
        var workflow = await ExecuteWorkflowAsync(operation, args, ct);
        if (workflow != null) return workflow;
        var upstream = await ExecuteUpstreamAsync(operation, args, ct);
        if (upstream != null) return upstream;
        string Text(string name, string fallback = "") => ToolArguments.Get(args, name, fallback);
        int Number(string name, int fallback = 0) => ToolArguments.Get(args, name, fallback);
        string? actionTarget = null;
        switch (operation)
        {
            case "open": case "new_tab":
                var url = Text("url", "about:blank");
                ValidateUrl(url);
                if (operation == "open" && url == "about:blank" && connection.ActiveTabId.Length > 0)
                    return ToolResult.Success(new { tabId = connection.ActiveTabId, attached = true, url = await EvaluateAsync("location.href", ct), verifyWith = "browser_state" });
                if (operation == "new_tab" || string.IsNullOrEmpty(connection.ActiveTabId)) await connection.NewTabAsync(ct);
                await InvalidateAsync(ct);
                var navigation = await connection.SendAsync("Page.navigate", new { url }, ct);
                if (navigation.TryGetProperty("errorText", out var navError)) return ToolResult.Failure(navError.GetString()!);
                await WaitReadyAsync(ct);
                return ToolResult.Success(new { tabId = connection.ActiveTabId, url, verifyWith = "browser_state" });
            case "state":
                if (connection.Dialog is { } pendingDialog) return ToolResult.Success(new { dialog = pendingDialog, activeTabId = connection.ActiveTabId });
                var offset = Number("offset");
                if (offset < 0) throw new ArgumentException("offset must be nonnegative.");
                if (offset == 0 || _snapshotObject == null || _snapshotTab != connection.ActiveTabId)
                {
                    await InvalidateAsync(ct);
                    var remote = await EvaluateAsync(ObserveScript, ct, false);
                    _snapshotObject = remote.GetProperty("objectId").GetString(); _snapshotTab = connection.ActiveTabId;
                    _page = await OnSnapshotAsync("function(){return this.info}", [], ct);
                    _dom = _page.GetProperty("dom").GetString()!;
                }
                var tabs = await connection.TabsAsync(ct); _tabs = tabs.Select(t => t.TabId).ToHashSet();
                offset = Math.Min(offset, _dom.Length); var end = Math.Min(offset + 14000, _dom.Length);
                return ToolResult.Success(new { url = _page.GetProperty("url"), title = _page.GetProperty("title"), activeTabId = connection.ActiveTabId, tabs,
                    dom = _dom[offset..end], nextOffset = end < _dom.Length ? (int?)end : null,
                    crossOriginFrames = _page.GetProperty("crossOriginFrames"), bounded = _page.GetProperty("bounded") });
            case "screenshot":
                var screenshot = await connection.SendAsync("Page.captureScreenshot", new { format = "png", captureBeyondViewport = false }, ct);
                return ToolResult.Success(new { imageBase64 = screenshot.GetProperty("data").GetString() });
            case "switch_tab": case "close_tab":
                var tab = Text("tabId");
                if (!_tabs.Contains(tab)) throw new InvalidOperationException("Use a tabId observed in browser_state.");
                await InvalidateAsync(ct);
                if (operation == "switch_tab") await connection.SwitchTabAsync(tab, ct); else await connection.CloseTabAsync(tab, ct);
                break;
            case "click": case "double_click": case "right_click": case "hover": case "input": case "select":
                var index = Number("index", -1);
                var element = await OnSnapshotAsync("""
                    function(i) {
                      const e=this.nodes[i]; if(!e || !e.isConnected) throw Error('Element is stale; read browser_state again');
                      if(e.disabled) throw Error('Element is disabled');
                      e.scrollIntoView({block:'center',inline:'center',behavior:'instant'});
                      const b=e.getBoundingClientRect(),root=e.getRootNode();
                      const hit=(root.elementFromPoint?root:e.ownerDocument).elementFromPoint(b.x+b.width/2,b.y+b.height/2);
                      const related=hit&&(e===hit||e.contains(hit)||hit.contains(e)||hit.closest('label')?.control===e||e.closest('label')?.control===hit);
                      if(!related)throw Error('Element is occluded; observe the page before clicking');
                      e.focus(); let r=e.getBoundingClientRect(), x=r.x+r.width/2,y=r.y+r.height/2,w=e.ownerDocument.defaultView;
                      while(w.frameElement) {const f=w.frameElement.getBoundingClientRect();x+=f.x;y+=f.y;w=w.parent;}
                      const editable=e.isContentEditable||['INPUT','TEXTAREA'].includes(e.tagName);
                      return {x,y,tag:e.tagName,type:e.type,label:(e.getAttribute('aria-label')||[...(e.labels||[])].map(l=>l.innerText).join(' ')||(!editable&&e.innerText)||e.placeholder||e.id||e.tagName).slice(0,120),identity:[e.tagName,e.id,e.name,e.getAttribute('aria-label'),!editable?e.innerText?.slice(0,100):'',i].join('|')};
                    }
                    """, [index], ct);
                actionTarget = element.GetProperty("label").GetString();
                if (operation == "select")
                    await OnSnapshotAsync("""
                        function(i,value){const e=this.nodes[i];if(e.tagName!=='SELECT')throw Error('Element is not a select');
                        const o=[...e.options].find(o=>o.value===value||o.text===value);if(!o)throw Error('Option not found');
                        e.value=o.value;e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));return true;}
                        """, [index, Text("text")], ct);
                else if (operation == "input")
                {
                    if (element.GetProperty("tag").GetString() is not ("INPUT" or "TEXTAREA") &&
                        !(await OnSnapshotAsync("function(i){return this.nodes[i].isContentEditable}", [index], ct)).GetBoolean())
                        throw new InvalidOperationException("Element is not an editable text field.");
                    if (element.TryGetProperty("type", out var type) && type.GetString() == "file") throw new InvalidOperationException("File input requires a file upload tool, not typing a path.");
                    if (ToolArguments.Get(args, "clear", true)) await KeysAsync("Control+a", ct);
                    await connection.SendAsync("Input.insertText", new { text = Text("text") }, ct);
                }
                else
                {
                    var x = element.GetProperty("x").GetDouble(); var y = element.GetProperty("y").GetDouble();
                    await connection.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
                    if (operation != "hover")
                    {
                        if (operation is "click" or "double_click") await GuardRepeatedClickAsync(element.GetProperty("identity").GetString()!, ct);
                        var button = operation == "right_click" ? "right" : "left";
                        for (var count = 1; count <= (operation == "double_click" ? 2 : 1); count++)
                        {
                            await connection.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button, clickCount = count }, ct);
                            await connection.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button, clickCount = count }, ct);
                        }
                    }
                }
                break;
            case "scroll":
                var amount = Number("amount", 600); var direction = Text("direction", "down");
                if (amount is < 1 or > 10000 || direction is not ("up" or "down" or "left" or "right")) throw new ArgumentException("Invalid scroll parameters.");
                await connection.SendAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x = 300, y = 300, deltaX = direction == "left" ? -amount : direction == "right" ? amount : 0, deltaY = direction == "up" ? -amount : direction == "down" ? amount : 0 }, ct);
                break;
            case "keys": await KeysAsync(Text("text"), ct); break;
            case "back": case "forward":
                var history = await connection.SendAsync("Page.getNavigationHistory", new { }, ct);
                var entries = history.GetProperty("entries"); var next = history.GetProperty("currentIndex").GetInt32() + (operation == "back" ? -1 : 1);
                if (next < 0 || next >= entries.GetArrayLength()) throw new InvalidOperationException("No history entry in that direction.");
                await connection.SendAsync("Page.navigateToHistoryEntry", new { entryId = entries[next].GetProperty("id").GetInt32() }, ct);
                await WaitReadyAsync(ct); break;
            case "reload": await connection.SendAsync("Page.reload", new { }, ct); await WaitReadyAsync(ct); break;
            case "evaluate":
                var evaluated = await EvaluateAsync(Text("script"), ct);
                await InvalidateAsync(ct);
                return ToolResult.Success(new { value = evaluated });
            case "dialog":
                await connection.SendAsync("Page.handleJavaScriptDialog", new { accept = ToolArguments.Get(args, "accept", false), promptText = Text("text") }, ct); break;
            case "close": await DisposeAsync(); return ToolResult.Success(new { closed = true });
            default: throw new ArgumentException("Unsupported browser operation.");
        }
        await InvalidateAsync(ct);
        return ToolResult.Success(new { completed = true, target = actionTarget, verifyWith = "browser_state" });
    }
    public static void ValidateUrl(string url)
    { if (url != "about:blank" && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("Use an http/https URL or about:blank."); }
    private async Task WaitReadyAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            try { if ((await EvaluateAsync("document.readyState", ct)).GetString() is "interactive" or "complete") return; }
            catch (InvalidOperationException) { }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("Page did not become ready. Observe state before retrying navigation.");
    }
    private async Task KeysAsync(string shortcut, CancellationToken ct)
    {
        var parts = shortcut.Split('+'); var key = parts[^1]; var modifiers = 0;
        foreach (var part in parts[..^1]) modifiers |= part.ToLowerInvariant() switch { "alt" => 1, "ctrl" or "control" => 2, "meta" or "cmd" => 4, "shift" => 8, _ => throw new ArgumentException("Unknown key modifier.") };
        var code = key switch { "Enter" => 13, "Tab" => 9, "Escape" => 27, "Backspace" => 8, "Delete" => 46, "ArrowLeft" => 37, "ArrowUp" => 38, "ArrowRight" => 39, "ArrowDown" => 40, "Home" => 36, "End" => 35, "PageUp" => 33, "PageDown" => 34, "Space" => 32, _ when key.Length == 1 => char.ToUpperInvariant(key[0]), _ => throw new ArgumentException("Unsupported key.") };
        await connection.SendAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, modifiers, windowsVirtualKeyCode = code, text = modifiers == 0 ? key == "Enter" ? "\r" : key.Length == 1 ? key : "" : "" }, ct);
        await connection.SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, modifiers, windowsVirtualKeyCode = code }, ct);
    }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
