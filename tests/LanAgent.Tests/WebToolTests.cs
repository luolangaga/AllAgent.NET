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
    public async Task SearchMapsDaemonContractAndIsAvailableThroughGeneratedDispatch()
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("http://127.0.0.1:3210/search", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("test query", body.GetProperty("query").GetString());
            Assert.Equal(3, body.GetProperty("limit").GetInt32());
            Assert.Equal("bing", body.GetProperty("engines")[0].GetString());
            return Response("{\"status\":\"ok\",\"data\":{\"results\":[{\"title\":\"Title\",\"url\":\"https://example.com\"}]}}");
        }));
        var tools = new WebTools("http://127.0.0.1:3210", client, client);
        var registry = new ToolRegistry(); registry.Register(tools);
        Assert.All(tools.Definitions, d => Assert.Equal(ToolRisk.Read, d.Risk));
        var result = await registry.CallAsync("web_search", JsonSerializer.SerializeToElement(new { query = "test query", limit = 3, engines = new[] { "bing" } }), default);
        Assert.True(result.Ok);
        Assert.Contains("https://example.com", result.ToJson());
        Assert.Contains("untrusted", result.ToJson());
    }

    [Fact]
    public async Task SearchReportsUpstreamErrorAndCapsBody()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Response("{\"status\":\"error\",\"error\":{\"code\":\"browser_unavailable\"}}"))));
        Assert.Contains("browser_unavailable", (await new WebTools("http://127.0.0.1:3210", client, client).SearchAsync("query")).Error);
        using var partial = new HttpClient(new Handler(_ => Task.FromResult(Response("{\"status\":\"ok\",\"data\":{\"totalResults\":0,\"results\":[],\"partialFailures\":[{\"engine\":\"bing\",\"message\":\"redirect failed\"}]}}"))));
        var failure = await new WebTools("http://127.0.0.1:3210", partial, partial).SearchAsync("query");
        Assert.False(failure.Ok); Assert.Contains("redirect failed", failure.Error);
        using var large = new HttpClient(new Handler(_ => Task.FromResult(Response(new string('x', 70_000)))));
        Assert.Contains("too large", (await new WebTools("http://127.0.0.1:3210", large, large).SearchAsync("query")).Error);
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
        Assert.Throws<ArgumentException>(() => new WebTools("http://example.com", client, client));
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
