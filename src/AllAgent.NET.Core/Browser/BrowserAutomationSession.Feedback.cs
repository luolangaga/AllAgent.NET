using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Browser;

public sealed partial class BrowserAutomationSession
{
    private readonly HashSet<string> _feedbackInstalled = [];
    private readonly Dictionary<string, List<JsonElement>> _recentFeedback = [];
    private string? _lastClickKey;
    private string? _lastClickState;
    private int _identicalClicks;

    // Record at DOM mutation time, rather than hoping a toast still exists when
    // the next LLM request reaches browser_state. No request/response interception.
    private const string FeedbackScript = """
        (() => {
          if (window.__allagentFeedback_v1) return;
          const queue = [], observed = new WeakSet(), captured = new WeakMap(), frames = new WeakSet();
          const stats={batches:0,discoveryNodes:0};
          function scan(root,budget,visit) {
            if(root.nodeType===1){visit(root);budget--;}
            const walker=document.createTreeWalker(root,NodeFilter.SHOW_ELEMENT);
            let e;while(budget-->0&&(e=walker.nextNode()))visit(e);
          }
          const selector = '[role="alert"],[role="status"],[aria-live]:not([aria-live="off"]),[class*="toast" i],[class*="message" i],[class*="notification" i],[class*="error" i]';
          function record(kind,text) {
            text=String(text||'').replace(/\s+/g,' ').trim().slice(0,600); if(!text)return;
            const now=Date.now(), last=queue[queue.length-1];
            if(last&&last.kind===kind&&last.text===text&&now-last.timestamp<1000)return;
            queue.push({kind,text,timestamp:now}); if(queue.length>32)queue.shift();
          }
          function capture(node,descend=true) {
            const e=node.nodeType===1?node:node.parentElement; if(!e)return;
            const relevant=e.closest(selector);
            const candidates = relevant?[relevant]:[];
            if(descend)scan(e,128,item=>{if(candidates.length<30&&item.matches(selector))candidates.push(item);});
            for(const item of candidates) {
              const view=item.ownerDocument.defaultView, style=view.getComputedStyle(item);
              if(style.display!=='none'&&style.visibility!=='hidden'&&item.getClientRects().length) {
                const text=item.innerText||item.textContent;
                if(captured.get(item)!==text){captured.set(item,text);record(item.matches('[role="alert"],[class*="error" i]')?'alert':'page_message',text);}
              } else captured.delete(item);
            }
          }
          function watch(root) {
            if(observed.has(root))return; observed.add(root);
            new MutationObserver(changes=>{
              stats.batches++;
              let budget=200;
              for(const change of changes.slice(0,200)) {
                capture(change.target,false);
                for(const node of change.addedNodes) {
                  if(budget--<=0)break;
                  capture(node);
                  if(node.nodeType===1)discover(node,128);
                }
              }
            }).observe(root,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['class','style','hidden','aria-hidden']});
            root.addEventListener('invalid',e=>record('validation',e.target.validationMessage),true);
          }
          function discover(root,budget=5000) {
            scan(root,budget,e=>{
              stats.discoveryNodes++;
              if(e.shadowRoot)watch(e.shadowRoot);
              if(e.tagName==='IFRAME') {
                try{if(e.contentDocument)watch(e.contentDocument);}catch{}
                if(!frames.has(e)){frames.add(e);e.addEventListener('load',()=>{try{if(e.contentDocument){watch(e.contentDocument);discover(e.contentDocument);}}catch{}});}
              }
            });
          }
          window.__allagentFeedback_v1={drain:()=>queue.splice(0),selector,stats};
          watch(document); discover(document);
          window.addEventListener('error',e=>record('javascript_error',e.message));
          window.addEventListener('unhandledrejection',e=>record('javascript_error',e.reason?.message||String(e.reason)));
        })()
        """;

    public async Task<ToolResult> ExecuteAsync(string operation, JsonElement args, CancellationToken ct)
    {
        if (operation == "close") return await ExecuteCoreAsync(operation, args, ct);
        if (connection.Dialog == null && connection.ActiveTabId.Length > 0) await InstallFeedbackAsync(ct);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await ExecuteCoreAsync(operation, args, ct);
        if (connection.Dialog == null && connection.ActiveTabId.Length > 0)
        {
            await InstallFeedbackAsync(ct);
            // Capture asynchronous submit feedback on this very tool response.
            // The observer keeps later/shorter messages for subsequent state reads.
            if (operation is "click" or "double_click" or "keys" or "select")
            {
                for (var i = 0; i < 10 && connection.Dialog == null; i++)
                {
                    await Task.Delay(200, ct);
                    await CollectFeedbackAsync(ct);
                    if (RecentFeedback().Any(e => e.GetProperty("timestamp").GetDouble() >= started)) break;
                }
            }
            await CollectFeedbackAsync(ct);
        }
        var fields = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        var data = fields.ValueKind == JsonValueKind.Object ? fields.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : new Dictionary<string, object?> { ["result"] = result.Data };
        data["recentMessages"] = RecentFeedback();
        if (operation is "click" or "double_click" or "keys" or "select")
        {
            data["actionMessages"] = RecentFeedback().Where(e => e.GetProperty("timestamp").GetDouble() >= started).ToArray();
            data["outcome"] = "Input was dispatched; this does not prove the website saved or accepted it. Read messages and verify the resulting state before retrying.";
        }
        return result with { Data = data };
    }

    private async Task InstallFeedbackAsync(CancellationToken ct)
    {
        if (_feedbackInstalled.Add(connection.ActiveTabId))
        {
            try { await connection.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = FeedbackScript }, ct); }
            catch { _feedbackInstalled.Remove(connection.ActiveTabId); throw; }
        }
        await EvaluateAsync(FeedbackScript, ct);
    }

    private async Task CollectFeedbackAsync(CancellationToken ct)
    {
        if (connection.Dialog != null) return;
        var messages = await EvaluateAsync("window.__allagentFeedback_v1?.drain() || []", ct);
        if (messages.ValueKind != JsonValueKind.Array) return;
        if (!_recentFeedback.TryGetValue(connection.ActiveTabId, out var buffer)) _recentFeedback[connection.ActiveTabId] = buffer = [];
        buffer.AddRange(messages.EnumerateArray().Select(m => m.Clone()));
        if (buffer.Count > 24) buffer.RemoveRange(0, buffer.Count - 24);
        // A closed tab's event log need not survive indefinitely.
        if (_recentFeedback.Count > 16)
            foreach (var key in _recentFeedback.Keys.Where(k => k != connection.ActiveTabId).Take(_recentFeedback.Count - 16).ToArray()) _recentFeedback.Remove(key);
    }

    private JsonElement[] RecentFeedback() => _recentFeedback.GetValueOrDefault(connection.ActiveTabId)?.ToArray() ?? [];

    private async Task GuardRepeatedClickAsync(string identity, CancellationToken ct)
    {
        var state = await EvaluateAsync("""
            (()=>{let text='',visited=0;const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);let n;
              while(text.length<20000&&visited++<10000&&(n=walker.nextNode())){
                if(!n.parentElement?.closest('script,style,'+(window.__allagentFeedback_v1?.selector||'[role="alert"]')))text+=n.textContent;
              }
              return JSON.stringify([location.href,text.slice(0,20000),
                [...document.querySelectorAll('input,textarea,select,[contenteditable="true"]')].slice(0,500).map(e=>e.type==='password'?'[redacted]':[e.value,e.checked,e.isContentEditable?e.textContent:''])]);})()
            """, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.GetString() ?? "")));
        var key = connection.ActiveTabId + ":" + identity;
        _identicalClicks = key == _lastClickKey && hash == _lastClickState ? _identicalClicks + 1 : 1;
        _lastClickKey = key; _lastClickState = hash;
        if (_identicalClicks >= 3)
            throw new InvalidOperationException("Stopped repeating the same click on an unchanged form. Inspect browser_state.recentMessages and validation errors; correct the input or explain the blocker. Do not reopen, reload, or use evaluate to bypass this guard.");
    }
}
