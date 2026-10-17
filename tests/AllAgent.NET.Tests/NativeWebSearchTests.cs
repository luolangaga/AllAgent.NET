using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class NativeWebSearchTests
{
    private static HttpResponseMessage Page(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(text, Encoding.UTF8, "text/html") };
    private static string Bing(string url, string title = "Result") => "<li class='b_algo'><h2><a href='" + url + "'>" + title + "</a></h2><div class='b_caption'><p>A useful snippet</p></div></li>";
    private static JsonElement Data(ToolResult result) => JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    [Fact]
    public async Task NativeSearchFallsBackInOrderAndStopsAtTheFirstReadableEngine()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls.Add(request.RequestUri!.IdnHost);
            if (request.RequestUri.IdnHost == "duckduckgo.com") return Page("unavailable", HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri.IdnHost == "html.duckduckgo.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("q=test+query", await request.Content!.ReadAsStringAsync(ct));
                return Page("<html>No readable matches</html>");
            }
            Assert.Equal("www.bing.com", request.RequestUri.IdnHost);
            Assert.Contains("q=test%20query", request.RequestUri.AbsoluteUri);
            return Page(Bing("https://example.com/article"));
        }));
        var result = await new NativeWebSearch(client).SearchAsync("test query", 5, null, default);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { "duckduckgo.com", "html.duckduckgo.com", "www.bing.com" }, calls);
        var data = Data(result);
        Assert.Equal("allagent-built-in", data.GetProperty("source").GetString());
        Assert.True(data.GetProperty("untrusted").GetBoolean());
        Assert.Equal(new[] { "duckduckgo", "bing" }, data.GetProperty("engines").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("no_results", Assert.Single(data.GetProperty("partialFailures").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("https://example.com/article", Assert.Single(data.GetProperty("results").EnumerateArray()).GetProperty("url").GetString());
    }

    [Fact]
    public async Task HostWithoutBrowserRejectsSearchWithoutSilentlyUsingHttpFallback()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Page("unexpected")); }));
        var result = await new WebTools("", client, client).SearchAsync("test query");
        Assert.False(result.Ok); Assert.Contains("Browser", result.Error); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExplicitEnginesAreNormalizedAndDeduplicateUrlsAcrossEnginesBeforeApplyingTheLimit()
    {
        var calls = new ConcurrentBag<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.IdnHost);
            return Task.FromResult(request.RequestUri.IdnHost == "www.bing.com"
                ? Page(Bing("https://example.com/shared", "Bing shared") + Bing("https://example.com/bing"))
                : Page("<div id='content_left'><div><h3><a href='https://example.com/shared'>Baidu shared</a></h3></div><div><h3><a href='https://example.com/baidu'>Baidu unique</a></h3></div></div>"));
        }));
        var result = await new NativeWebSearch(client).SearchAsync("query", 3, ["BING", "baidu", "bing"], default);
        Assert.True(result.Ok, result.Error); Assert.Equal(2, calls.Count);
        var data = Data(result); var entries = data.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { "https://example.com/shared", "https://example.com/bing", "https://example.com/baidu" }, entries.Select(e => e.GetProperty("url").GetString()));
        Assert.Equal("Bing shared", entries[0].GetProperty("title").GetString());
        Assert.Equal(3, data.GetProperty("totalResults").GetInt32());
        Assert.Empty(data.GetProperty("partialFailures").EnumerateArray());
    }

    [Theory]
    [InlineData("duckduckgo", "<div class='result result--ad'><a class='result__a' href='https://ads.example.com'>Ad</a></div><div class='result'><a class='result__a' href='//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Farticle'>Title &amp; details</a><span class='result__snippet'>Readable <b>snippet</b></span></div>")]
    [InlineData("bing", "<li class='b_algo b_ad'><h2><a href='https://ads.example.com'>Ad</a></h2></li><li class='b_algo'><h2><a href='https://example.com/article'>Title &amp; details</a></h2><div class='b_caption'><p>Readable <b>snippet</b></p></div></li>")]
    [InlineData("baidu", "<div id='content_left'><div><h3><a href='https://example.com/article'>Title &amp; details</a></h3><div class='c-font-normal' aria-label='Readable snippet'>Other text</div></div></div>")]
    public void HtmlParsingExtractsReadableResultsAndUnwrapsTrackingLinks(string engine, string html)
    {
        var result = Assert.Single(NativeWebSearch.ParseHtml(engine, html, 5));
        Assert.Equal("Title & details", result.Title); Assert.Equal("Readable snippet", result.Description);
        Assert.Equal("https://example.com/article", result.Url); Assert.Equal("example.com", result.Source); Assert.Equal(engine, result.Engine);
    }

    [Fact]
    public void DuckDuckGoFeedIsParsedAsJsonWithoutEvaluatingSurroundingJavaScript()
    {
        var script = """
            throw new Error('must never execute'); DDG.pageLayout.load('d', [
              {"u":"https://example.com/article","t":"<b>Title &amp; details</b>","a":"Readable <em>snippet</em>"},
              {"u":"https://example.com/article","t":"Duplicate"},
              {"u":"http://127.0.0.1/secrets","t":"Private"},
              {"u":"https://example.com/ad","t":"Ad","n":1},
              {"u":"https://example.com/second","t":"Second"}
            ]); dangerousOperation();
            """;
        var entries = NativeWebSearch.ParseDuckDuckGoFeed(script, 5);
        Assert.Equal(2, entries.Count); Assert.Equal("Title & details", entries[0].Title);
        Assert.Equal("Readable snippet", entries[0].Description); Assert.Equal("https://example.com/second", entries[1].Url);
        Assert.Single(NativeWebSearch.ParseDuckDuckGoFeed(script, 1));
        Assert.Empty(NativeWebSearch.ParseDuckDuckGoFeed("someOtherFunction([])", 5));
    }

    [Fact]
    public async Task DuckDuckGoPreloadUsesOnlyTheTrustedFeedAndAvoidsHtmlFallbackWhenItSucceeds()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.RequestUri.IdnHost == "duckduckgo.com"
                ? Page("<link rel='preload' href='https://attacker.example/d.js'><script id='deep_preload_script' src='https://links.duckduckgo.com/d.js?x=1&amp;y=2'></script>")
                : Page("DDG.pageLayout.load('d', [{\"u\":\"https://example.com/article\",\"t\":\"Feed title\"}]);"));
        }));
        var result = await new NativeWebSearch(client).SearchAsync("query", 5, ["duckduckgo"], default);
        Assert.True(result.Ok, result.Error); Assert.Equal(2, calls.Count);
        Assert.Equal("https://links.duckduckgo.com/d.js?x=1&y=2", calls[1]);
        Assert.Equal("Feed title", Assert.Single(Data(result).GetProperty("results").EnumerateArray()).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("https://attacker.example/page")]
    [InlineData("https://duckduckgo.com/")]
    [InlineData("http://www.bing.com/search")]
    [InlineData("https://www.bing.com:444/search")]
    [InlineData("https://user:password@www.bing.com/search")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public async Task RedirectCannotLeaveTheSelectedEngineHttpsOrigin(string target)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++; var response = Page("", HttpStatusCode.Redirect); response.Headers.Location = new Uri(target); return Task.FromResult(response);
        }));
        var result = await new NativeWebSearch(client).SearchAsync("query", 5, ["bing"], default);
        Assert.False(result.Ok); Assert.Equal(1, calls); Assert.Contains("trusted engine origin", result.Error);
        Assert.Equal("engine_error", Assert.Single(Data(result).GetProperty("partialFailures").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PartialEngineFailureStillReturnsOtherEngineResultsAndDiagnostics()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.IdnHost == "www.bing.com"
            ? Page("upstream down", HttpStatusCode.ServiceUnavailable)
            : Page("<div id='content_left'><div><h3><a href='https://example.com/success'>Available</a></h3></div></div>"))));
        var result = await new NativeWebSearch(client).SearchAsync("query", 5, ["bing", "baidu"], default);
        Assert.True(result.Ok, result.Error); var data = Data(result);
        Assert.Single(data.GetProperty("results").EnumerateArray());
        var failure = Assert.Single(data.GetProperty("partialFailures").EnumerateArray());
        Assert.Equal("bing", failure.GetProperty("engine").GetString()); Assert.Equal("engine_error", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBecomingAnEngineFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, ct) =>
        { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Page("unreachable"); }));
        var pending = new NativeWebSearch(client).SearchAsync("query", 5, ["bing"], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BaiduPrefersOrganicDestinationAndSkipsPaidRedirects()
    {
        var entries = NativeWebSearch.ParseHtml("baidu", """
            <div id='content_left'>
              <div><h3><a href='https://www.baidu.com/baidu.php?url=ad'>Advertisement</a></h3></div>
              <div mu='https://example.com/article'><a href='https://www.baidu.com/link?url=tracking'>Link</a><h3>Article title</h3><div class='c-abstract'>Article details</div></div>
            </div>
            """, 5);
        var entry = Assert.Single(entries);
        Assert.Equal("https://example.com/article", entry.Url);
        Assert.Equal("Article title", entry.Title);
        Assert.Equal("Article details", entry.Description);
    }

    [Fact]
    public async Task LargeEnginePagesAndAggregatedUnicodeResultsRemainBounded()
    {
        var html = "<html>" + new string(' ', 600_000) + string.Join("", Enumerable.Range(0, 10).Select(i => Bing("https://example.com/" + i + "?x=" + new string('a', 7500), new string('中', 200)).Replace("A useful snippet", new string('文', 600)))) + "</html>";
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Page(html))));
        var result = await new NativeWebSearch(client).SearchAsync(new string('查', 2000), 10, ["bing"], default);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.Truncated);
        Assert.InRange(Data(result).GetProperty("totalResults").GetInt32(), 1, 9);
        Assert.True(Encoding.UTF8.GetByteCount(result.ToJson()) < 49_000);
    }

    [Fact]
    public async Task OversizedEnginePageFailsWithDiagnostics()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Page(new string('x', 2_000_001)))));
        var result = await new NativeWebSearch(client).SearchAsync("query", 5, ["bing"], default);
        Assert.False(result.Ok);
        Assert.Contains("page size limit", result.Error);
    }

    [Fact]
    public async Task UnsupportedEngineAndPrecancelledCallsNeverContactHttp()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP call")));
        var search = new NativeWebSearch(client);
        Assert.False((await search.SearchAsync("query", 5, ["unrecognized"], default)).Ok);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("query", 5, null, cancellation.Token));
    }
}
