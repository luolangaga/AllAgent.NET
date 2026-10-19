using System.Net;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Extensions;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Plugin web extensions: manifest validation, contribution snapshot, bridge namespacing,
/// the web soft switch and the plugin asset server's guards.</summary>
public sealed class PluginWebTests
{
    private const string FullManifest = """
    {"sdkVersion":1,"assembly":"AllAgent.NET.SamplePlugin.dll","entryType":"AllAgent.NET.SamplePlugin.ExamplePlugin",
     "web":{"sdkVersion":1,"capabilities":["notifications","storage"],"contributes":{
       "pages":[{"id":"dashboard","title":"示例仪表盘","icon":"gauge","entry":"web/dashboard.html","order":40}],
       "toolCards":[{"tool":"example_clock","entry":"web/card.html"}],
       "commands":[{"id":"open-dashboard","title":"打开示例仪表盘","page":"dashboard"}]}}}
    """;

    private static async Task<(PluginHost Host, string Directory)> LoadAsync(TestWorkspace workspace, string manifest, Action<string>? prepare = null)
    {
        var data = Path.Combine(workspace.Root, "data");
        var directory = Path.Combine(data, "plugins", "allagent.net.sampleplugin-1.0.0-web00001");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sample-plugin", "AllAgent.NET.SamplePlugin.dll"), Path.Combine(directory, "AllAgent.NET.SamplePlugin.dll"), true);
        await File.WriteAllTextAsync(Path.Combine(directory, "allagent-plugin.json"), manifest);
        prepare?.Invoke(directory);
        var host = new PluginHost(data);
        host.Save(new InstalledPlugin("AllAgent.NET.SamplePlugin", "1.0.0", Path.GetFileName(directory)));
        await host.LoadEnabledAsync();
        return (host, directory);
    }

    private static void WriteWeb(string directory, params string[] files)
    {
        Directory.CreateDirectory(Path.Combine(directory, "web"));
        foreach (var file in files) File.WriteAllText(Path.Combine(directory, "web", file), "<html>" + file + "</html>");
    }

    [Fact]
    public async Task WebContributionsAreValidatedAndExposedInTheSnapshot()
    {
        using var w = new TestWorkspace();
        var (host, _) = await LoadAsync(w, FullManifest, directory => WriteWeb(directory, "dashboard.html", "card.html"));
        await using (host)
        {
            var status = Assert.Single(host.Status);
            Assert.Null(status.Error);
            Assert.NotNull(status.Web);
            Assert.True(status.Web!.WebEnabled);
            Assert.Equal(1, status.Web.Pages);
            Assert.Equal(1, status.Web.ToolCards);
            Assert.Contains("notifications", status.Web.Capabilities);

            var snapshot = host.WebSnapshot("http://127.0.0.1:1234");
            Assert.Equal("http://127.0.0.1:1234", snapshot.AssetBaseUrl);
            var info = Assert.Single(snapshot.Plugins);
            Assert.True(info.Active);
            Assert.Equal("dashboard", Assert.Single(info.Contributes.Pages!).Id);
            Assert.Equal("web/dashboard.html", Assert.Single(info.Contributes.Pages!).Entry);
            Assert.Equal("example_clock", Assert.Single(info.Contributes.ToolCards!).Tool);
            Assert.Equal("dashboard", Assert.Single(info.Contributes.Commands!).Page);
            Assert.Contains("storage", info.Capabilities);

            Assert.NotNull(host.WebAssetRoot("AllAgent.NET.SamplePlugin"));
            Assert.True(host.WebBridgeHandlers.ContainsKey("plugin.allagent.net.sampleplugin.history"));
        }
    }

    [Fact]
    public async Task EntriesOutsideTheWebFolderDisableOnlyTheWebPart()
    {
        using var w = new TestWorkspace();
        var manifest = FullManifest.Replace("web/dashboard.html", "../escape.html");
        var (host, _) = await LoadAsync(w, manifest, directory => WriteWeb(directory, "card.html"));
        await using (host)
        {
            // The plugin itself keeps working; only its web payload is switched off with a reason.
            var status = Assert.Single(host.Status);
            Assert.Null(status.Error);
            Assert.NotNull(status.Web!.Error);
            Assert.Contains("必须位于 web/ 目录内", status.Web.Error);
            Assert.Contains("example_clock", host.PluginTools.Select(item => item.Tool.Name));
            Assert.False(Assert.Single(host.WebSnapshot(null).Plugins).Active);
            Assert.Null(host.WebAssetRoot("AllAgent.NET.SamplePlugin"));
            Assert.Empty(host.WebBridgeHandlers);
        }
    }

    [Fact]
    public async Task ANewerWebSdkVersionDisablesWebWithAReason()
    {
        using var w = new TestWorkspace();
        var manifest = FullManifest.Replace("\"web\":{\"sdkVersion\":1", "\"web\":{\"sdkVersion\":2");
        var (host, _) = await LoadAsync(w, manifest, directory => WriteWeb(directory, "dashboard.html", "card.html"));
        await using (host)
        {
            var status = Assert.Single(host.Status);
            Assert.Null(status.Error);
            Assert.NotNull(status.Web!.Error);
            Assert.Contains("宿主版本更高", status.Web.Error);
        }
    }

    [Fact]
    public async Task WebBridgeHandlersFollowBothSwitches()
    {
        using var w = new TestWorkspace();
        var (host, _) = await LoadAsync(w, FullManifest, directory => WriteWeb(directory, "dashboard.html", "card.html"));
        await using (host)
        {
            var handler = host.WebBridgeHandlers["plugin.allagent.net.sampleplugin.history"];
            var result = (JsonElement)(await handler(JsonSerializer.SerializeToElement(new { limit = 5 }), default))!;
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            Assert.Empty(result.GetProperty("items").EnumerateArray());

            // The web soft switch removes handlers without unloading the plugin.
            host.SetWebEnabled("AllAgent.NET.SamplePlugin", false);
            Assert.Empty(host.WebBridgeHandlers);
            Assert.Null(host.WebAssetRoot("AllAgent.NET.SamplePlugin"));
            Assert.False(Assert.Single(host.WebSnapshot(null).Plugins).Active);
            Assert.Contains("example_clock", host.PluginTools.Select(item => item.Tool.Name));

            host.SetWebEnabled("AllAgent.NET.SamplePlugin", true);
            Assert.True(host.WebBridgeHandlers.ContainsKey("plugin.allagent.net.sampleplugin.history"));

            // Disabling the whole plugin removes the web part with it.
            await host.SetEnabledAsync("AllAgent.NET.SamplePlugin", false);
            Assert.Empty(host.WebBridgeHandlers);
            Assert.Null(host.WebAssetRoot("AllAgent.NET.SamplePlugin"));
        }
    }

    [Fact]
    public async Task WebEnabledDefaultsToTrueAndPersistsTheSoftSwitch()
    {
        using var w = new TestWorkspace();
        var data = Path.Combine(w.Root, "data");
        var directory = Path.Combine(data, "plugins", "allagent.net.sampleplugin-1.0.0-web00001");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(data, "plugins", "installed.json"), $$"""[{"PackageId":"AllAgent.NET.SamplePlugin","Version":"1.0.0","Directory":"{{Path.GetFileName(directory)}}"}]""");
        await using (var first = new PluginHost(data))
        {
            Assert.True(Assert.Single(first.Installed).WebEnabled);
            first.SetWebEnabled("AllAgent.NET.SamplePlugin", false);
        }
        await using var second = new PluginHost(data);
        Assert.False(Assert.Single(second.Installed).WebEnabled);
    }

    [Fact]
    public async Task PluginAssetServerServesSdkAndPayloadsAndRejectsEscapes()
    {
        using var w = new TestWorkspace();
        var webRoot = Path.Combine(w.Root, "web");
        Directory.CreateDirectory(Path.Combine(webRoot, "sdk"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "sdk", "allagent-plugin-sdk.js"), "window.AllAgentPlugin = { connect() {} };");
        var (host, directory) = await LoadAsync(w, FullManifest, target =>
        {
            WriteWeb(target, "dashboard.html", "card.html");
            File.WriteAllText(Path.Combine(target, "secret.txt"), "must not be served");
        });
        await using (host)
        {
            await using var server = PluginAssetServer.TryStart(host, webRoot);
            Assert.NotNull(server);
            using var client = new HttpClient();
            var baseUrl = server!.BaseUrl;

            var sdk = await client.GetAsync(baseUrl + "/__sdk/allagent-plugin-sdk.js");
            Assert.Equal(HttpStatusCode.OK, sdk.StatusCode);
            Assert.Contains("AllAgentPlugin", await sdk.Content.ReadAsStringAsync());

            var asset = await client.GetAsync(baseUrl + "/p/AllAgent.NET.SamplePlugin/web/dashboard.html");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("text/html; charset=utf-8", asset.Content.Headers.ContentType?.ToString());
            Assert.Equal("nosniff", asset.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-cache", asset.Headers.GetValues("Cache-Control").Single());

            var missing = await client.GetAsync(baseUrl + "/p/AllAgent.NET.SamplePlugin/web/none.html");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // Files inside the install directory but outside web/ stay unreachable.
            var escape = await client.GetAsync(baseUrl + "/p/AllAgent.NET.SamplePlugin/secret.txt");
            Assert.Equal(HttpStatusCode.Forbidden, escape.StatusCode);

            var unknown = await client.GetAsync(baseUrl + "/p/No.Such.Plugin/web/dashboard.html");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/p/AllAgent.NET.SamplePlugin/web/dashboard.html"));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);

            var post = await client.PostAsync(baseUrl + "/p/AllAgent.NET.SamplePlugin/web/dashboard.html", new StringContent("x"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

            // A Host header that does not name the loopback listener is never served.
            using (var spoofed = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/__sdk/allagent-plugin-sdk.js"))
            {
                spoofed.Headers.Host = "evil.example";
                var response = await client.SendAsync(spoofed);
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            }

            // The web soft switch stops asset serving immediately.
            host.SetWebEnabled("AllAgent.NET.SamplePlugin", false);
            var disabled = await client.GetAsync(baseUrl + "/p/AllAgent.NET.SamplePlugin/web/dashboard.html");
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            Assert.True(File.Exists(Path.Combine(directory, "web", "dashboard.html")));
        }
    }
}
