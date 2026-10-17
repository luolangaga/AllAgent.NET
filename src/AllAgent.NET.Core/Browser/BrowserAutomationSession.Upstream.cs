using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Browser;

public sealed partial class BrowserAutomationSession
{
    // Ported from browser-use tools/service.py at df1e21533e323912e79a109146b711dddde8cf23.
    private async Task<ToolResult?> ExecuteUpstreamAsync(string operation, JsonElement args, CancellationToken ct)
    {
        string Text(string name, string fallback = "") => ToolArguments.Get(args, name, fallback);
        int Number(string name, int fallback) => ToolArguments.Get(args, name, fallback);
        bool Flag(string name, bool fallback = false) => ToolArguments.Get(args, name, fallback);
        string Quote(object value) => JsonSerializer.Serialize(value);
        switch (operation)
        {
            case "web_search":
                var engine = Text("engine", "bing");
                var searchUrl = engine switch
                {
                    "bing" => "https://www.bing.com/search?q=",
                    "google" => "https://www.google.com/search?q=",
                    "duckduckgo" => "https://duckduckgo.com/?q=",
                    _ => throw new ArgumentException("Unsupported search engine.")
                } + Uri.EscapeDataString(Text("query"));
                var opened = await ExecuteAsync("new_tab", JsonSerializer.SerializeToElement(new { url = searchUrl }), ct);
                if (!opened.Ok) return opened;
                var script = SearchResultsScript.Replace("__LIMIT__", Math.Clamp(Number("limit", 5), 1, 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var page = await EvaluateAsync(script, ct);
                    if (page.GetProperty("blocked").GetBoolean())
                        return ToolResult.Failure("Search requires browser verification. Complete the CAPTCHA/consent in the browser and retry.");
                    if (page.GetProperty("results").GetArrayLength() > 0)
                        return ToolResult.Success(new { source = "browser", engine, query = Text("query"), results = page.GetProperty("results").Clone(), untrusted = true });
                    await Task.Delay(500, ct);
                }
                return ToolResult.Failure("No search results could be read. Inspect the browser for consent, verification, network errors or changed page layout.");
            case "search":
                var query = Uri.EscapeDataString(Text("query")).Replace("%20", "+");
                var url = Text("engine", "bing").ToLowerInvariant() switch
                {
                    "duckduckgo" => "https://duckduckgo.com/?q=" + query,
                    "google" => "https://www.google.com/search?q=" + query + "&udm=14",
                    "bing" => "https://www.bing.com/search?q=" + query,
                    _ => throw new ArgumentException("Unsupported search engine: duckduckgo, google or bing required.")
                };
                return await ExecuteAsync("open", JsonSerializer.SerializeToElement(new { url }), ct);
            case "navigate":
                return await ExecuteAsync(Flag("new_tab") ? "new_tab" : "open", JsonSerializer.SerializeToElement(new { url = Text("url") }), ct);
            case "wait":
                var seconds = Math.Clamp(Number("seconds", 3) - 1, 0, 30);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return ToolResult.Success(new { waitedSeconds = seconds });
            case "dropdown_options":
                var options = await OnSnapshotAsync("""
                    function(i){const e=this.nodes[i];if(!e?.isConnected)throw Error('Element is stale');
                    if(e.tagName!=='SELECT')throw Error('Element is not a native select');
                    return {options:[...e.options].map(o=>({text:o.text,value:o.value,selected:o.selected,disabled:o.disabled})),multiple:e.multiple};}
                    """, [Number("index", -1)], ct);
                return ToolResult.Success(options);
            case "find_text":
                var found = await EvaluateAsync("""
                    (()=>{const text=__LAN_TEXT__;const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);
                    let n;while(n=walker.nextNode()){if(n.textContent.toLowerCase().includes(text.toLowerCase())&&n.parentElement?.getClientRects().length){
                    n.parentElement.scrollIntoView({block:'center',behavior:'instant'});return true;}}return false;})()
                    """.Replace("__LAN_TEXT__", Quote(Text("text")), StringComparison.Ordinal), ct);
                await InvalidateAsync(ct);
                return found.GetBoolean() ? ToolResult.Success(new { found = true }) : ToolResult.Failure("Text not found on page.");
            case "find_elements":
                var selector = Quote(Text("selector"));
                var attributes = args.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array ? attrs.GetRawText() : "[]";
                var count = Math.Clamp(Number("max_results", 50), 1, 200);
                var elements = await EvaluateAsync($"[...document.querySelectorAll({selector})].slice(0,{count}).map(e=>({{tag:e.tagName.toLowerCase(),text:{(Flag("include_text", true) ? "(e.innerText||e.textContent||'').slice(0,500)" : "''")},attributes:Object.fromEntries({attributes}.map(a=>[a,e.getAttribute(a)]))}}))", ct);
                return ToolResult.Success(new { elements });
            case "search_page":
                var scope = Text("css_scope");
                var source = await EvaluateAsync(scope.Length == 0 ? "document.body?.innerText || ''" : $"document.querySelector({Quote(scope)})?.innerText || ''", ct);
                var content = source.GetString() ?? "";
                var pattern = Text("pattern");
                if (pattern.Length == 0) throw new ArgumentException("pattern must not be empty.");
                var regex = new Regex(Flag("regex") ? pattern : Regex.Escape(pattern), Flag("case_sensitive") ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
                var context = Math.Clamp(Number("context_chars", 150), 0, 500);
                var matches = regex.Matches(content).Cast<Match>().Take(Math.Clamp(Number("max_results", 25), 1, 50))
                    .Select(match => new { offset = match.Index, text = match.Value, context = content[Math.Max(0, match.Index - context)..Math.Min(content.Length, match.Index + match.Length + context)] }).ToArray();
                return ToolResult.Success(new { matches });
            default: return null;
        }
    }
}
