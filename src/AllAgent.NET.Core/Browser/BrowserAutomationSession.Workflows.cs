using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Browser;

public sealed partial class BrowserAutomationSession
{
    private sealed record Locator(string Tab, string Url, string Selector, string Role, string Name);
    private readonly Dictionary<string, Locator> _references = [];

    private async Task<int> ResolveAsync(Locator locator, CancellationToken ct)
    {
        if (locator.Tab != connection.ActiveTabId) throw new InvalidOperationException("reference_tab_mismatch: switch to the observed tab first.");
        if ((await EvaluateAsync("location.href", ct)).GetString() != locator.Url) throw new InvalidOperationException("reference_expired: page URL changed; locate the new page again.");
        await ExecuteCoreAsync("state", JsonSerializer.SerializeToElement(new { }), ct);
        var found = await OnSnapshotAsync("""
            function(selector,role,name){const matches=[];
              for(let i=0;i<this.nodes.length;i++){const e=this.nodes[i];
                const r=e.getAttribute('role')||({BUTTON:'button',A:'link',INPUT:'textbox',TEXTAREA:'textbox',SELECT:'combobox'}[e.tagName]||'');
                const n=(e.getAttribute('aria-label')||[...(e.labels||[])].map(l=>l.innerText).join(' ')||e.innerText||e.placeholder||'').trim();
                if((!selector||e.matches(selector))&&(!role||r===role)&&(!name||n===name))matches.push(i);}
              if(matches.length!==1)throw Error('locator_ambiguous_or_missing: expected one visible element, found '+matches.length);
              return matches[0];}
            """, [locator.Selector, locator.Role, locator.Name], ct);
        return found.GetInt32();
    }

    private Locator Reference(JsonElement args)
    {
        var id = ToolArguments.Get(args, "ref", "");
        return _references.TryGetValue(id, out var locator) ? locator : throw new InvalidOperationException("reference_expired: call locate again.");
    }

    private async Task<ToolResult?> ExecuteWorkflowAsync(string operation, JsonElement args, CancellationToken ct)
    {
        switch (operation)
        {
            case "locate":
                var locator = new Locator(connection.ActiveTabId, (await EvaluateAsync("location.href", ct)).GetString()!, ToolArguments.Get(args, "selector", ""), ToolArguments.Get(args, "role", ""), ToolArguments.Get(args, "name", ""));
                if (locator.Selector.Length == 0 && locator.Name.Length == 0) throw new ArgumentException("Provide an observed selector or accessible name.");
                await ResolveAsync(locator, ct);
                if (_references.Count >= 256) _references.Remove(_references.Keys.First());
                var id = Guid.NewGuid().ToString("N"); _references[id] = locator;
                return ToolResult.Success(new { @ref = id, tabId = locator.Tab, locator.Selector, locator.Role, locator.Name });
            case "act":
                var action = ToolArguments.Get(args, "action", "");
                if (action is not ("click" or "input" or "select" or "hover")) throw new ArgumentException("act supports click/input/select/hover.");
                var index = await ResolveAsync(Reference(args), ct);
                return await ExecuteAsync(action, JsonSerializer.SerializeToElement(new { index, text = ToolArguments.Get(args, "text", ""), clear = ToolArguments.Get(args, "clear", true) }), ct);
            case "fill_fields":
                if (!args.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 30)
                    throw new ArgumentException("fields must contain 1..30 {ref,text} entries.");
                // Validate the entire shape and every reference before causing side effects.
                foreach (var field in fields.EnumerateArray())
                {
                    Reference(field);
                    if (field.GetProperty("text").ValueKind != JsonValueKind.String || field.GetProperty("text").GetString()!.Length > 20000)
                        throw new ArgumentException("Each field requires text of at most 20000 characters.");
                }
                var completed = new List<string>();
                foreach (var field in fields.EnumerateArray())
                {
                    try
                    {
                        var target = await ResolveAsync(Reference(field), ct);
                        await ExecuteCoreAsync("input", JsonSerializer.SerializeToElement(new { index = target, text = field.GetProperty("text").GetString(), clear = true }), ct);
                        target = await ResolveAsync(Reference(field), ct);
                        var actual = await OnSnapshotAsync("function(i){const e=this.nodes[i];return e.isContentEditable?e.textContent:e.value;}", [target], ct);
                        if (actual.GetString() != field.GetProperty("text").GetString()) throw new InvalidOperationException("input_value_mismatch: field did not retain the requested text; inspect the editor before continuing.");
                        completed.Add(field.GetProperty("ref").GetString()!);
                    }
                    catch (InvalidOperationException ex)
                    { return ToolResult.Failure(ex.Message) with { Data = new { completed, failedRef = field.GetProperty("ref").GetString(), rolledBack = false } }; }
                }
                return ToolResult.Success(new { completed, verifyWith = "observe or state; input dispatch does not prove framework persistence" });
            case "click_and_wait":
                var success = ToolArguments.Get(args, "success_text", "");
                var errorSelector = ToolArguments.Get(args, "error_selector", "[role=alert]");
                if (success.Length == 0 && errorSelector.Length == 0) throw new ArgumentException("Provide a success or error condition.");
                var timeout = Math.Clamp(ToolArguments.Get(args, "timeout_ms", 8000), 200, 20000);
                // Baseline old alerts: a previous error must not be attributed to this click.
                var baseline = await EvaluateAsync($"[...document.querySelectorAll({JsonSerializer.Serialize(errorSelector.Length == 0 ? "[role=alert]" : errorSelector)})].filter(e=>e.getClientRects().length).map(e=>e.innerText)", ct);
                var prior = baseline.EnumerateArray().Select(e => e.GetString()).ToHashSet();
                var priorSuccess = success.Length > 0 && (await EvaluateAsync($"(document.body?.innerText||'').includes({JsonSerializer.Serialize(success)})", ct)).GetBoolean();
                var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var clicked = await ExecuteAsync("act", JsonSerializer.SerializeToElement(new { @ref = args.GetProperty("ref").GetString(), action = "click" }), ct);
                if (!clicked.Ok) return clicked;
                var until = DateTime.UtcNow.AddMilliseconds(timeout);
                do
                {
                    if (connection.Dialog != null) return ToolResult.Success(new { outcome = "dialog_pending", dialog = connection.Dialog, verifyWith = "Use dialog operation; do not click the submit button again." });
                    await CollectFeedbackAsync(ct);
                    var observation = await EvaluateAsync($"({{success:{(success.Length == 0 ? "false" : "(document.body?.innerText||'').includes(" + JsonSerializer.Serialize(success) + ")")},errors:[...document.querySelectorAll({JsonSerializer.Serialize(errorSelector.Length == 0 ? "[role=alert]" : errorSelector)})].filter(e=>e.getClientRects().length).map(e=>e.innerText)}})", ct);
                    var freshError = observation.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && !prior.Contains(e));
                    // A transient alert may have vanished already; keep its original message.
                    var transient = RecentFeedback().Where(e => e.GetProperty("timestamp").GetDouble() >= started).Select(e => e.GetProperty("text").GetString()).ToArray();
                    if (observation.GetProperty("success").GetBoolean() && !priorSuccess) return ToolResult.Success(new { outcome = "success_observed", evidence = success });
                    if (success.Length > 0 && transient.Any(t => t?.Contains(success, StringComparison.Ordinal) == true)) return ToolResult.Success(new { outcome = "success_observed", evidence = success });
                    if (freshError != null) return ToolResult.Success(new { outcome = "error_observed", message = freshError });
                    var alert = RecentFeedback().FirstOrDefault(e => e.GetProperty("timestamp").GetDouble() >= started && e.GetProperty("kind").GetString() == "alert" && !prior.Contains(e.GetProperty("text").GetString()));
                    if (alert.ValueKind == JsonValueKind.Object) return ToolResult.Success(new { outcome = "error_observed", message = alert.GetProperty("text") });
                    await Task.Delay(150, ct);
                } while (DateTime.UtcNow < until);
                return ToolResult.Success(new { outcome = "unknown", automaticRetry = false, verifyWith = "state and recentMessages; do not resubmit blindly" });
            case "observe":
                var scope = ToolArguments.Get(args, "selector", "body");
                var limit = Math.Clamp(ToolArguments.Get(args, "limit", 4000), 100, 14000);
                var text = await EvaluateAsync($"(()=>{{const e=document.querySelector({JsonSerializer.Serialize(scope)});if(!e)throw Error('scope_missing');return (e.innerText||'').slice(0,{limit});}})()", ct);
                return ToolResult.Success(new { text, scope, fullStateAvailable = true });
            default: return null;
        }
    }
}
