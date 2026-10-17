using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Terminal interaction")]
public class BrowserSearchTests
{
    [Fact]
    public async Task BingSearchLaunchesBrowserAndReadsResults()
    {
        if (Environment.GetEnvironmentVariable("ALLAGENT_LIVE_SEARCH_TEST") != "1") return;
        using var workspace = new TestWorkspace();
        var state = new BrowserUseState { Root = Path.Combine(workspace.Root, "search-browser") };
        var browser = new BrowserUseTools(state);
        try
        {
        var tools = new WebTools(browser);
        var result = await tools.SearchAsync("Microsoft dotnet documentation", 3);
        Assert.True(result.Ok, result.ToJson());
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal("bing", data.GetProperty("engine").GetString());
        Assert.InRange(data.GetProperty("results").GetArrayLength(), 1, 3);
        Assert.True(data.GetProperty("untrusted").GetBoolean());
        }
        finally
        {
            await browser.DisposeAsync();
            for (var i = 0; i < 50 && Directory.Exists(state.Root); i++)
            {
                try { Directory.Delete(state.Root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
    }
}
