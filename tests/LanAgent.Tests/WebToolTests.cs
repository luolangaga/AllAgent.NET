using System.Net;
using System.Text;
using System.Text.Json;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class WebToolTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    private static HttpResponseMessage Response(string body, string media = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, media) };

    [Fact]
    public async Task SearchNeverCallsLegacyHttpService()
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("Legacy HTTP search called")));
        var tools = new WebTools("http://127.0.0.1:3210", client, client);
        var result = await tools.SearchAsync("query");
        Assert.False(result.Ok);
        Assert.Contains("Browser search is unavailable", result.Error);
    }

    [Fact]
    public async Task SearchRespectsDisabledBrowserAndValidatesEngine()
    {
        var state = new BrowserUseState();
        state.Configure(new BrowserUseConfiguration(Enabled: false));
        await using var browser = new BrowserUseTools(state);
        var tools = new WebTools(browser);
        var registry = new ToolRegistry(); registry.Register(tools);
        var result = await registry.CallAsync("web_search", JsonSerializer.SerializeToElement(new { query = "test query" }), default);
        Assert.False(result.Ok);
        Assert.Contains("disabled", result.Error);
        Assert.False((await tools.SearchAsync("query", engines: ["bad"])).Ok);
        Assert.False((await tools.SearchAsync("query", engines: ["bing", "google"])).Ok);
    }

    [Fact]
    public async Task FetchExtractsHtmlWithoutScriptsAndMarksTruncation()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Response("<html><head><title>Example &amp; title</title></head><body><script>ignore me</script><p>Readable &amp; useful</p><p>" + new string('a', 2000) + "</p></body></html>", "text/html"))));
        var registry = new ToolRegistry(); registry.Register(new WebTools(null, client, client));
        var result = await registry.CallAsync("web_fetch", JsonSerializer.SerializeToElement(new { url = "https://example.com/article", max_characters = 1000 }), default);
        Assert.True(result.Ok); Assert.True(result.Truncated);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal("Example & title", data.GetProperty("title").GetString());
        Assert.StartsWith("Readable & useful", data.GetProperty("content").GetString());
        Assert.Equal(1000, data.GetProperty("content").GetString()!.Length);
        Assert.DoesNotContain("ignore me", result.ToJson());
    }

    [Fact]
    public async Task RedirectCannotFetchPrivateTarget()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = Response("", status: HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return Task.FromResult(response);
        }));
        var result = await new WebTools(null, client, client).FetchAsync("https://example.com");
        Assert.False(result.Ok); Assert.Equal(1, calls);
        Assert.Contains("private", result.Error);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://localhost/")]
    [InlineData("http://127.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://server.local/")]
    [InlineData("https://user:password@example.com/")]
    public void PrivateUrlsAreRejected(string url) => Assert.False(WebTools.TryPublicUrl(url, out _));

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void DnsPrivateAddressesAreRejected(string ip) => Assert.False(WebTools.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task InvalidInputsDoNotMakeNetworkCalls()
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("Unexpected network access")));
        var tools = new WebTools(null, client, client);
        Assert.False((await tools.SearchAsync(" ")).Ok);
        Assert.False((await tools.SearchAsync("query", 11)).Ok);
        Assert.False((await tools.FetchAsync("http://127.0.0.1")).Ok);
        Assert.False((await tools.FetchAsync("https://example.com", 999)).Ok);

    }

    [Fact]
    public async Task FetchSupportsRelativeRedirectsAndRejectsBinaryContent()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                var response = Response("", status: HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("/article", UriKind.Relative);
                return Task.FromResult(response);
            }
            return Task.FromResult(Response("article", "text/plain"));
        }));
        var result = await new WebTools(null, client, client).FetchAsync("https://example.com/start");
        Assert.True(result.Ok); Assert.Contains("https://example.com/article", result.ToJson());
        using var binary = new HttpClient(new Handler(_ => Task.FromResult(Response("binary", "application/pdf"))));
        Assert.False((await new WebTools(null, binary, binary).FetchAsync("https://example.com/file.pdf")).Ok);
    }
}
