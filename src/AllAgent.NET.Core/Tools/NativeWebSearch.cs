using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AllAgent.NET.Core.Tools;

internal sealed record WebSearchEntry(string Title, string Url, string Description, string Source, string Engine);
internal sealed record WebSearchFailure(string Engine, string Code, string Message);

/// <summary>In-process search over public engine pages; no JavaScript or external worker runs.</summary>
internal sealed class NativeWebSearch(HttpClient client)
{
    internal static readonly string[] SupportedEngines = ["duckduckgo", "bing", "baidu"];
    private const int PageLimit = 2_000_000;

    public async Task<ToolResult> SearchAsync(string query, int limit, string[]? engines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var automatic = engines is not { Length: > 0 };
        var selected = automatic ? SupportedEngines : engines!.Select(e => e.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Any(e => !SupportedEngines.Contains(e, StringComparer.Ordinal)))
            return ToolResult.Failure("Built-in search supports duckduckgo, bing and baidu. Omit engines for automatic fallback.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var outcomes = new List<(List<WebSearchEntry> Entries, WebSearchFailure? Failure)>();
        if (automatic)
        {
            foreach (var engine in selected)
            {
                var outcome = await RunEngineAsync(engine, query, limit, deadline.Token, ct);
                outcomes.Add(outcome);
                if (outcome.Entries.Count > 0) break;
            }
        }
        else outcomes.AddRange(await Task.WhenAll(selected.Select(engine => RunEngineAsync(engine, query, limit, deadline.Token, ct))));
        ct.ThrowIfCancellationRequested();
        var results = outcomes.SelectMany(o => o.Entries).DistinctBy(e => e.Url, StringComparer.Ordinal).Take(limit).ToArray();
        var failures = outcomes.Where(o => o.Failure != null).Select(o => o.Failure!).ToArray();
        object Data() => new { source = "allagent-built-in", query, engines = outcomes.Select((_, i) => selected[i]).ToArray(), totalResults = results.Length, results, partialFailures = failures, untrusted = true };
        var truncated = false;
        while (results.Length > 0 && JsonSerializer.SerializeToUtf8Bytes(Data(), ToolResult.JsonOptions).Length > 48_000)
        {
            results = results[..^1];
            truncated = true;
        }
        var data = Data();
        if (results.Length == 0 && failures.Length > 0)
            return new ToolResult(false, data, "Search did not produce results: " + string.Join("; ", failures.Select(f => f.Engine + ": " + f.Message)));
        return ToolResult.Success(data, truncated);
    }

    private async Task<(List<WebSearchEntry> Entries, WebSearchFailure? Failure)> RunEngineAsync(string engine, string query, int limit, CancellationToken deadline, CancellationToken caller)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            List<WebSearchEntry> entries;
            if (engine == "duckduckgo") entries = await DuckDuckGoAsync(query, limit, timeout.Token);
            else
            {
                var uri = engine == "bing"
                    ? new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=10&setlang=en-US")
                    : new Uri("https://www.baidu.com/s?wd=" + Uri.EscapeDataString(query) + "&ie=utf-8&tn=88093251_62_hao_pg");
                entries = ParseHtml(engine, await ReadPageAsync(uri, engine, timeout.Token), limit);
            }
            return entries.Count > 0 ? (entries, null) : (entries, new(engine, "no_results", "No readable results; the query may have no matches or the engine may require verification."));
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return ([], new(engine, "timeout", "Engine request timed out.")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or RegexMatchTimeoutException)
        { return ([], new(engine, "engine_error", ToolResult.Clip(ex.Message, 300))); }
    }

    private async Task<List<WebSearchEntry>> DuckDuckGoAsync(string query, int limit, CancellationToken ct)
    {
        // Parse the engine's JSON data feed as data; never evaluate its JavaScript.
        try
        {
            var page = await ReadPageAsync(new Uri("https://duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&ia=web"), "duckduckgo", ct);
            var document = Document(page);
            var links = document.DocumentNode.SelectNodes("//link[@rel='preload' and @href] | //script[@id='deep_preload_script' and @src]");
            foreach (var node in links ?? Enumerable.Empty<HtmlNode>())
            {
                var href = HtmlEntity.DeEntitize(node.GetAttributeValue("href", node.GetAttributeValue("src", "")));
                if (!Uri.TryCreate(href, UriKind.Absolute, out var feed) || feed.Scheme != "https" || feed.IdnHost != "links.duckduckgo.com" || feed.Port != 443 || feed.UserInfo != "" || feed.AbsolutePath != "/d.js") continue;
                var entries = ParseDuckDuckGoFeed(await ReadPageAsync(feed, "duckduckgo", ct), limit);
                if (entries.Count > 0) return entries;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException) { /* The HTML endpoint is an independent fallback. */ }
        var html = await ReadPageAsync(new Uri("https://html.duckduckgo.com/html/"), "duckduckgo", ct, query);
        return ParseHtml("duckduckgo", html, limit);
    }

    private async Task<string> ReadPageAsync(Uri uri, string engine, CancellationToken ct, string? formQuery = null)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!AllowedEngineUrl(engine, uri)) throw new InvalidOperationException("Search redirect or feed left the trusted engine origin.");
            using var request = new HttpRequestMessage(formQuery == null ? HttpMethod.Get : HttpMethod.Post, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.5");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            if (formQuery != null) request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = formQuery });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location == null || !Uri.TryCreate(uri, response.Headers.Location, out var next) || !AllowedEngineUrl(engine, next))
                    throw new InvalidOperationException("Search redirect left the trusted engine origin or requires verification.");
                if ((int)response.StatusCode is 301 or 302 or 303) formQuery = null;
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            var (text, truncated) = await WebTools.ReadBoundedAsync(response, PageLimit, ct);
            if (truncated) throw new InvalidOperationException("Search response exceeded the page size limit.");
            return text;
        }
        throw new InvalidOperationException("Search engine redirected too many times.");
    }

    internal static bool AllowedEngineUrl(string engine, Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo == "" && (engine switch
    {
        "duckduckgo" => uri.IdnHost is "duckduckgo.com" or "html.duckduckgo.com" or "links.duckduckgo.com",
        "bing" => uri.IdnHost is "www.bing.com" or "cn.bing.com",
        "baidu" => uri.IdnHost == "www.baidu.com",
        _ => false
    });

    internal static List<WebSearchEntry> ParseHtml(string engine, string html, int limit)
    {
        var document = Document(html);
        var path = engine switch
        {
            "duckduckgo" => "//div[" + Class("result") + " and not(" + Class("result--ad") + ")]",
            "bing" => "//li[" + Class("b_algo") + " and not(" + Class("b_ad") + ")]",
            "baidu" => "//*[@id='content_left']/*[.//h3]",
            _ => throw new ArgumentException("Unknown search engine.")
        };
        var entries = new List<WebSearchEntry>();
        foreach (var card in document.DocumentNode.SelectNodes(path) ?? Enumerable.Empty<HtmlNode>())
        {
            var anchor = engine == "duckduckgo" ? card.SelectSingleNode(".//a[" + Class("result__a") + "]") : card.SelectSingleNode(engine == "bing" ? ".//h2//a[@href]" : ".//h3//a[@href]") ?? (engine == "baidu" ? card.SelectSingleNode(".//a[@href]") : null);
            if (anchor == null) continue;
            var rawUrl = anchor.GetAttributeValue("href", "");
            // Some Baidu layouts put the title and link in separate elements.
            if (engine == "baidu" && rawUrl.Length == 0) rawUrl = card.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", "") ?? "";
            if (engine == "baidu")
            {
                // Baidu marks paid redirects separately and often provides an organic destination in mu.
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var redirect) && redirect.IdnHost == "www.baidu.com" && redirect.AbsolutePath == "/baidu.php") continue;
                var destination = HtmlEntity.DeEntitize(card.GetAttributeValue("mu", ""));
                if (WebTools.TryPublicUrl(destination, out _)) rawUrl = destination;
            }
            var snippet = engine switch
            {
                "duckduckgo" => card.SelectSingleNode(".//*[" + Class("result__snippet") + "]"),
                "bing" => card.SelectSingleNode(".//*[" + Class("b_caption") + "]//p") ?? card.SelectSingleNode(".//*[" + Class("b_caption") + "]"),
                _ => card.SelectSingleNode(".//*[@aria-label and (" + Class("c-font-normal") + ")]") ?? card.SelectSingleNode(".//*[" + Class("cos-row") + "]") ?? card.SelectSingleNode(".//*[" + Class("c-abstract") + "]")
            };
            var description = snippet?.GetAttributeValue("aria-label", snippet.InnerText) ?? "";
            Add(entries, engine, rawUrl, engine == "baidu" ? card.SelectSingleNode(".//h3")?.InnerText ?? anchor.InnerText : anchor.InnerText, description, limit);
            if (entries.Count >= limit) break;
        }
        return entries;
    }

    internal static List<WebSearchEntry> ParseDuckDuckGoFeed(string script, int limit)
    {
        var marker = Regex.Match(script, @"DDG\.pageLayout\.load\(\s*['""]d['""]\s*,\s*", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!marker.Success) return [];
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(script[(marker.Index + marker.Length)..]));
        using var json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        var entries = new List<WebSearchEntry>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("n", out _)) continue;
            string Value(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            Add(entries, "duckduckgo", Value("u"), Value("t"), Value("a"), limit);
            if (entries.Count >= limit) break;
        }
        return entries;
    }

    private static void Add(List<WebSearchEntry> results, string engine, string rawUrl, string title, string description, int limit)
    {
        if (results.Count >= limit) return;
        var url = NormalizeResultUrl(engine, HtmlEntity.DeEntitize(rawUrl));
        title = Text(title);
        if (url == null || title.Length == 0 || results.Any(e => e.Url == url.AbsoluteUri)) return;
        results.Add(new(ToolResult.Clip(title, 200), url.AbsoluteUri, ToolResult.Clip(Text(description), 600), url.IdnHost, engine));
    }

    internal static Uri? NormalizeResultUrl(string engine, string raw)
    {
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (raw.StartsWith('/')) raw = (engine == "duckduckgo" ? "https://duckduckgo.com" : engine == "bing" ? "https://www.bing.com" : "https://www.baidu.com") + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        string? Parameter(string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            { var pair = part.Split('=', 2); if (pair.Length == 2 && pair[0] == name) return Uri.UnescapeDataString(pair[1].Replace('+', ' ')); }
            return null;
        }
        var host = uri.IdnHost;
        if (engine == "duckduckgo" && (host == "duckduckgo.com" || host.EndsWith(".duckduckgo.com", StringComparison.Ordinal)) && Parameter("uddg") is { } target)
            return WebTools.TryPublicUrl(target, out var direct) ? direct : null;
        if (engine == "bing" && (host == "bing.com" || host.EndsWith(".bing.com", StringComparison.Ordinal)) && uri.AbsolutePath.StartsWith("/ck/", StringComparison.Ordinal))
        {
            var encoded = Parameter("u");
            if (encoded == null || encoded.Length > 12000) return null;
            if (encoded.StartsWith("a1", StringComparison.Ordinal)) encoded = encoded[2..];
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            try { var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))); return WebTools.TryPublicUrl(decoded, out var direct) ? direct : null; }
            catch (FormatException) { return null; }
        }
        if ((host == "duckduckgo.com" || host.EndsWith(".duckduckgo.com", StringComparison.Ordinal) || host == "bing.com" || host.EndsWith(".bing.com", StringComparison.Ordinal)) && uri.AbsolutePath is "/search" or "/" or "/html/") return null;
        return WebTools.TryPublicUrl(uri.AbsoluteUri, out var result) ? result : null;
    }

    private static string Class(string name) => "contains(concat(' ', normalize-space(@class), ' '), ' " + name + " ')";
    private static HtmlDocument Document(string html) { var document = new HtmlDocument(); document.LoadHtml(html); return document; }
    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Document(html).DocumentNode.InnerText), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}
