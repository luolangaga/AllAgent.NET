using System.Net;
using System.IO.Compression;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using System.Text.Json;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class ToolStoreTests
{
    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillArchiveInstallsSupportFilesAndRejectsTraversal(bool malicious)
    {
        var root = Path.Combine(Path.GetTempPath(), "lanagent-store-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("repo/skills/demo/SKILL.md").Open())) writer.Write("---\nname: demo\ndescription: Test skill\n---\n\nTest instructions.");
                using var support = new StreamWriter(archive.CreateEntry("repo/skills/demo/" + (malicious ? "../escape.txt" : "references/help.md")).Open()); support.Write("support");
            }
            using var http = new HttpClient(new ArchiveHandler(buffer.ToArray()));
            using var store = new DataStore(Path.Combine(root, "data"));
            var catalog = new SkillCatalog(store, root, Path.Combine(root, "data"), root);
            using var json = JsonDocument.Parse("{\"skillId\":\"demo\"}");
            var item = new StoreItem("org/repo/demo", "demo", "org/repo", "", "", json.RootElement);
            if (malicious)
            {
                await Assert.ThrowsAsync<IOException>(() => new ToolStoreClient(http).InstallSkill(item, catalog, "project", default));
                Assert.False(Directory.Exists(Path.Combine(catalog.ProjectDirectory, "demo")));
            }
            else
            {
                Assert.Equal("demo", await new ToolStoreClient(http).InstallSkill(item, catalog, "project", default));
                Assert.Equal("support", File.ReadAllText(Path.Combine(catalog.ProjectDirectory, "demo", "references", "help.md")));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
    [Fact]
    public async Task FailedDirectoryNeverReturnsSeedData()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.ServiceUnavailable, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ToolStoreClient(http).Search(false, "react"));
    }
    [Fact]
    public async Task FailedRecommendationsNeverReturnPreFilledRows()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.ServiceUnavailable, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ToolStoreClient(http).RecommendSkills());
    }
    [Fact]
    public async Task TrendingHomePreservesLiveOrderAndBuildsInstallIdentity()
    {
        const string json = """{"skills":[{"source":"org/repo","skillId":"first","name":"first","installs":200},{"source":"org/repo","skillId":"second","name":"second","installs":100}]}""";
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, json));
        var results = await new ToolStoreClient(http).RecommendSkills();
        Assert.Equal(new[] { "org/repo/first", "org/repo/second" }, results.Items.Select(i => i.Id));
        Assert.Contains("趋势榜", results.Basis); Assert.Null(results.NextCursor);
    }
    [Fact]
    public async Task EmptyDirectoryStaysEmpty()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, "{\"skills\":[]}"));
        Assert.Empty((await new ToolStoreClient(http).Search(false, "react")).Items);
    }
    [Fact]
    public async Task MalformedDirectoryFailsInsteadOfUsingExamples()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, "not JSON"));
        await Assert.ThrowsAnyAsync<JsonException>(() => new ToolStoreClient(http).Search(true, "github"));
    }
    [Fact]
    public async Task McpResultsKeepCursorAndFilterDeletedServers()
    {
        const string json = """{"servers":[{"server":{"name":"org/active","remotes":[{"type":"streamable-http","url":"https://example.com/mcp"}]}},{"server":{"name":"org/deleted"},"_meta":{"io.modelcontextprotocol.registry/official":{"status":"deleted"}}}],"metadata":{"nextCursor":"cursor"}}""";
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, json));
        var result = await new ToolStoreClient(http).Search(true, "example");
        Assert.Single(result.Items); Assert.Equal("cursor", result.NextCursor);
        var config = ToolStoreClient.McpConfig(result.Items[0]);
        Assert.False(config.IsEnabled); Assert.Equal("http", config.Transport); Assert.Equal("https://example.com/mcp", config.Command);
    }
    [Fact]
    public void NpmConfigPinsVersionAndRetainsRequiredEnvironment()
    {
        using var json = JsonDocument.Parse("""{"packages":[{"registryType":"npm","identifier":"@example/mcp","version":"1.2.3","transport":{"type":"stdio"},"environmentVariables":[{"name":"API_KEY"}]}]}""");
        var config = ToolStoreClient.McpConfig(new("org/tool", "tool", "org/tool", "", "", json.RootElement));
        Assert.Equal(new[] { "-y", "@example/mcp@1.2.3" }, config.ArgumentList); Assert.Equal("${API_KEY}", config.EnvironmentVariables["API_KEY"]); Assert.False(config.IsEnabled);
    }
    [Fact]
    public void UnsupportedMcpDoesNotProduceFakeConfiguration()
    {
        using var json = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentException>(() => ToolStoreClient.McpConfig(new("org/tool", "tool", "org/tool", "", "", json.RootElement)));
    }
    [Theory]
    [InlineData("/skills store")]
    [InlineData("/mcp store")]
    public void StoresAreRegisteredCommands(string command) => Assert.Contains(CommandCatalog.Builtins, c => c.Text == command);
}

