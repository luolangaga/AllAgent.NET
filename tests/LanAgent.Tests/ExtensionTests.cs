using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class ExtensionTests
{
    internal static byte[] Package(string tags = "LanAgent", string manifestPath = "lanagent/lanagent-plugin.json", bool dependency = false)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var nuspec = archive.CreateEntry("example.nuspec"); using (var writer = new StreamWriter(nuspec.Open())) writer.Write($"<package><metadata><id>LanAgent.SamplePlugin</id><version>1.0.0</version><authors>fixture</authors><description>fixture</description><tags>{tags}</tags>{(dependency ? "<dependencies><group targetFramework=\"net10.0\"><dependency id=\"Fixture.Dependency\" version=\"[1.0.0]\" /></group></dependencies>" : "")}</metadata></package>");
            var dll = archive.CreateEntry("lib/net10.0/LanAgent.SamplePlugin.dll"); using (var stream = dll.Open()) stream.Write(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "sample-plugin", "LanAgent.SamplePlugin.dll")));
            var manifest = archive.CreateEntry(manifestPath); using (var writer = new StreamWriter(manifest.Open())) writer.Write(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample-plugin", "lanagent-plugin.json")));
        }
        return memory.ToArray();
    }
    [Fact]
    public async Task ActualDllPackageLoadsToolsProvidersCommandsHooksAndDisablesCleanly()
    {
        using var w = new TestWorkspace(); var data = Path.Combine(w.Root, "data"); var path = Path.Combine(w.Root, "sample.nupkg"); await File.WriteAllBytesAsync(path, Package());
        var installer = new NuGetPluginInstaller(data); var package = await installer.ImportAsync(path);
        await using var host = new PluginHost(data); host.Save(package); await host.LoadEnabledAsync();
        var status = Assert.Single(host.Status); Assert.Null(status.Error); Assert.Equal("lanagent.example", status.Id);
        var turn = await host.Extensions.ConfigureTurnAsync(new(Guid.NewGuid(), w.Root, false, "task", "base", ["example_clock"], []), default);
        Assert.Contains("example_clock", turn.SystemPrompt); Assert.Contains("example-review", turn.Skills.Keys);
        var outgoing = await host.Extensions.ConfigureRequestAsync(new(turn.SessionId, false, 1, [new("user", "task")]), default);
        Assert.Contains(outgoing.Messages, m => m.Content == "The example plugin is active for this request.");
        var approved = 0; var registry = new ToolRegistry((_, _) => { approved++; return ValueTask.FromResult(false); }, hooks: host.Hooks); registry.Register(host.ToolSet());
        Assert.True((await registry.CallAsync("example_clock", JsonSerializer.SerializeToElement(new { }), default)).Ok);
        Assert.False((await registry.CallAsync("example_note", JsonSerializer.SerializeToElement(new { text = "must not write" }), default)).Ok); Assert.Equal(1, approved);
        Assert.False(File.Exists(Path.Combine(data, "plugins", package.Directory, "data", "note.txt")));
        Assert.Contains("你好，test", await Assert.Single(host.Commands).Execute(["test"], default));
        var provider = new AiProviderFactory(host.Providers).GetProvider("example"); var chunks = new List<StreamChunk>(); await foreach (var chunk in provider.StreamChatAsync(new(new ApiConfig(), [new("user", "proof")]))) chunks.Add(chunk);
        Assert.Contains(chunks, c => c.TextDelta?.Contains("proof") == true);
        Assert.True(File.Exists(Path.Combine(data, "plugins", package.Directory, "data", "lifecycle.jsonl")));
        host.SetHookEnabled("example.audit", false); Assert.False(Assert.Single(host.HookStatus).Enabled);
        await host.SetEnabledAsync(package.PackageId, false); registry.Replace<PluginToolSet>((PluginToolSet)host.ToolSet()); Assert.Empty(registry.Definitions); Assert.Empty(host.Providers); Assert.Empty(host.Commands);
        var disabled = await host.Extensions.ConfigureTurnAsync(new(turn.SessionId, w.Root, false, "task", "base", [], []), default);
        Assert.Equal("base", disabled.SystemPrompt); Assert.Empty(disabled.Skills);
        Assert.Single((await host.Extensions.ConfigureRequestAsync(new(turn.SessionId, false, 1, [new("user", "task")]), default)).Messages);
        await host.SetEnabledAsync(package.PackageId, true); Assert.Null(Assert.Single(host.Status).Error); Assert.False(Assert.Single(host.HookStatus).Enabled);
        await host.RemoveAsync(package.PackageId); Assert.Empty(host.Installed);
    }
    [Fact]
    public async Task ImportRejectsMissingTagAndZipTraversalBeforeLoadingDll()
    {
        using var w = new TestWorkspace(); var installer = new NuGetPluginInstaller(Path.Combine(w.Root, "data"));
        var path = Path.Combine(w.Root, "invalid.nupkg"); await File.WriteAllBytesAsync(path, Package("OtherAgent")); await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(path));
        await File.WriteAllBytesAsync(path, Package(manifestPath: "lanagent/../../escape/lanagent-plugin.json")); await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(path)); Assert.False(Directory.Exists(Path.Combine(w.Root, "escape")));
    }
    [Fact]
    public async Task NuGetDiscoveryUsesV3ResourcesAndExactTag()
    {
        using var w = new TestWorkspace(); using var client = new HttpClient(new FixtureHandler(request => request.RequestUri!.AbsolutePath == "/index.json" ? """
            {"resources":[{"@type":"SearchQueryService/3.5.0","@id":"https://fixture/query"}]}
            """ : """
            {"data":[{"id":"valid","version":"1.0.0","description":"plugin","authors":["author"],"tags":["LanAgent"],"totalDownloads":2},{"id":"wrong","version":"1.0.0","description":"not plugin","tags":["LanAgentOther"]}]}
            """));
        var result = await new NuGetPluginInstaller(w.Root, client, "https://fixture/index.json").SearchAsync("tools"); Assert.Equal("valid", Assert.Single(result).Id);
    }
    [Fact]
    public async Task HookVetoAndTimeoutPreventHostSideEffectsWhileAfterFailureIsObservable()
    {
        using var w = new TestWorkspace(); var hooks = new HookPipeline(() => [new("deny", (evt, _) => ValueTask.FromResult(new HookDecision(evt.Stage != HookStage.BeforeTool, "test policy")))]);
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true), hooks: hooks); registry.Register(w.Tools);
        var result = await registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = "blocked.txt", content = "proof" }), default); Assert.False(result.Ok); Assert.Contains("test policy", result.Error); Assert.False(File.Exists(Path.Combine(w.Root, "blocked.txt")));
        var timeout = new HookPipeline(() => [new("stall", async (_, ct) => { await Task.Delay(5000, ct); return new(); })]) { Timeout = TimeSpan.FromMilliseconds(40) };
        await Assert.ThrowsAsync<TimeoutException>(() => timeout.NotifyAsync(new(HookStage.BeforeTurn), default).AsTask());
        await timeout.NotifyAsync(new(HookStage.AfterTurn), default); Assert.Contains(timeout.Errors, e => e.Contains("超时"));
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") }); }
    [Fact]
    public async Task HookDeadlineCoversSynchronousPluginCallbacks()
    {
        using var release = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new HookPipeline(() => [new("sync-stall", (_, _) =>
        {
            release.Wait(); completed.SetResult(); return ValueTask.FromResult(new HookDecision());
        })]) { Timeout = TimeSpan.FromMilliseconds(80) };
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => pipeline.NotifyAsync(new(HookStage.BeforeTurn), default).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    [Fact]
    public async Task V3InstallerFetchesTaggedPackageAndCompatibleDependencyWithoutExecutingScripts()
    {
        using var w = new TestWorkspace(); using var dependency = new MemoryStream();
        using (var zip = new ZipArchive(dependency, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("dep.nuspec").Open())) writer.Write("<package><metadata><id>Fixture.Dependency</id><version>1.0.0</version><authors>fixture</authors><description>dependency</description></metadata></package>");
            using (var writer = new StreamWriter(zip.CreateEntry("lib/net10.0/dependency-proof.txt").Open())) writer.Write("dependency proof");
            using (var writer = new StreamWriter(zip.CreateEntry("build/attack.targets").Open())) writer.Write("must not execute or extract");
        }
        using var http = new HttpClient(new BinaryHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/index.json" => Encoding.UTF8.GetBytes("""{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://fixture/flat/"}]}"""),
            "/flat/lanagent.sampleplugin/1.0.0/lanagent.sampleplugin.1.0.0.nupkg" => Package(dependency: true),
            "/flat/fixture.dependency/index.json" => Encoding.UTF8.GetBytes("""{"versions":["1.0.0"]}"""),
            "/flat/fixture.dependency/1.0.0/fixture.dependency.1.0.0.nupkg" => dependency.ToArray(),
            _ => throw new InvalidOperationException("Unexpected download " + request.RequestUri)
        }));
        var package = await new NuGetPluginInstaller(w.Root, http, "https://fixture/index.json").InstallAsync("LanAgent.SamplePlugin", "1.0.0");
        var folder = Path.Combine(w.Root, "plugins", package.Directory); Assert.Equal("dependency proof", File.ReadAllText(Path.Combine(folder, "dependency-proof.txt"))); Assert.False(Directory.Exists(Path.Combine(folder, "build")));
        using var hashes = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "packages.lock.json"))); Assert.Equal(2, hashes.RootElement.EnumerateObject().Count());
    }
    [Fact]
    public async Task RegistryClipsPluginStdoutAndStderrFields()
    {
        var registry = new ToolRegistry(); registry.Register(new OutputTools()); var result = await registry.CallAsync("fixture_output", JsonSerializer.SerializeToElement(new { }), default); Assert.True(result.Truncated);
        var data = JsonSerializer.SerializeToElement(result.Data); Assert.Equal(ToolResult.MaxOutputCharacters, data.GetProperty("stdout").GetString()!.Length); Assert.Equal(ToolResult.MaxOutputCharacters, data.GetProperty("stderr").GetString()!.Length);
    }
    private sealed class OutputTools : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new("fixture_output", "test output", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct) => ValueTask.FromResult(ToolResult.Success(new { stdout = new string('a', 25000), stderr = new string('b', 25000) }));
    }
    private sealed class BinaryHandler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)) }); }
}
