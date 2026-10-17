using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class TerminalAuditRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AllAgent-audit-regression-" + Guid.NewGuid().ToString("N"));
    public TerminalAuditRegressionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private AgentOptions Options => new() { WorkspaceRoot = root, SharedDirectory = Path.Combine(root, "shared"), AuditPath = null, ModelRetryDelay = TimeSpan.Zero };

    [Fact]
    public void OldManifestLoadsAndItsRevisionsSurviveNewCommits()
    {
        var directory = Path.Combine(root, "data");
        var session = new Conversation { Title = "original" };
        using (var store = new DataStore(directory)) store.AddConversation(session);
        File.Move(Path.Combine(directory, "allagent.json"), Path.Combine(directory, "lanagent.json"));
        File.Move(Path.Combine(directory, "allagent.json.bak"), Path.Combine(directory, "lanagent.json.bak"));
        var original = File.ReadAllBytes(Path.Combine(directory, "lanagent.json"));
        using (var store = new DataStore(directory))
        {
            Assert.Equal("original", store.GetConversation(session.Id)!.Title);
            for (var i = 0; i < 4; i++) store.AddMessage(session.Id, new Message { Role = "user", Content = "message " + i });
        }
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "lanagent.json")));
        using (var current = new DataStore(directory)) Assert.Equal(4, current.GetMessages(session.Id).Count);
        // Prove the preserved old manifest still has all its immutable revisions.
        File.Move(Path.Combine(directory, "allagent.json"), Path.Combine(directory, "current.saved"));
        File.Move(Path.Combine(directory, "allagent.json.bak"), Path.Combine(directory, "backup.saved"));
        using var legacy = new DataStore(directory);
        Assert.Equal("original", legacy.GetConversation(session.Id)!.Title);
        Assert.Empty(legacy.GetMessages(session.Id));
    }

    [Fact]
    public void BrokenLegacyDatabaseCannotBecomeAnEmptyStore()
    {
        File.WriteAllText(Path.Combine(root, "lanagent.json"), "broken");
        Assert.Throws<InvalidDataException>(() => new DataStore(root));
    }

    [Fact]
    public void LegacyBackupRecoversAndBrokenCurrentManifestsNeverFallBackToStaleData()
    {
        using (var store = new DataStore(root)) store.SetSetting("kept", "legacy");
        File.Move(Path.Combine(root, "allagent.json"), Path.Combine(root, "lanagent.json"));
        File.Move(Path.Combine(root, "allagent.json.bak"), Path.Combine(root, "lanagent.json.bak"));
        File.WriteAllText(Path.Combine(root, "lanagent.json"), "broken");
        using (var reader = new DataStore(root)) Assert.Equal("legacy", reader.GetSetting("kept"));
        File.WriteAllText(Path.Combine(root, "allagent.json"), "broken current");
        File.WriteAllText(Path.Combine(root, "allagent.json.bak"), "broken current backup");
        Assert.Throws<InvalidDataException>(() => new DataStore(root));
        Assert.Equal("broken current", File.ReadAllText(Path.Combine(root, "allagent.json")));
    }

    [Fact]
    public void ProjectIndexPreservesLegacyEntriesWhenPublishingNewIndex()
    {
        var oldWorkspace = Path.Combine(root, "previous"); var oldData = Path.Combine(oldWorkspace, ".lanagent", "data");
        var legacyPath = Path.Combine(root, "lanagent-projects.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new[] { new ProjectDataDirectory.ProjectEntry(oldWorkspace, oldData) }));
        var original = File.ReadAllBytes(legacyPath);
        ProjectDataDirectory.Register(oldWorkspace, oldData, root);
        ProjectDataDirectory.Register(root, Path.Combine(root, "data"), root);
        var entries = JsonSerializer.Deserialize<ProjectDataDirectory.ProjectEntry[]>(File.ReadAllText(Path.Combine(root, "allagent-projects.json")))!;
        Assert.Equal(2, entries.Length); Assert.Contains(entries, entry => entry.Workspace == oldWorkspace && entry.DataDirectory == oldData);
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void LegacyProjectSkillRemainsAvailableAndCurrentSkillOverridesIt()
    {
        using var store = new DataStore(Path.Combine(root, "data"));
        var previous = Path.Combine(root, ".lanagent", "skills", "fixture"); Directory.CreateDirectory(previous);
        var current = Path.Combine(root, ".allagent", "skills", "fixture");
        const string frontmatter = "---\nname: fixture\ndescription: fixture\n---\n";
        File.WriteAllText(Path.Combine(previous, "SKILL.md"), frontmatter + "previous instructions");
        var catalog = new SkillCatalog(store, root, Path.Combine(root, "shared"), root);
        Assert.Equal("previous instructions", catalog.Get("fixture").Instructions);
        Directory.CreateDirectory(current); File.WriteAllText(Path.Combine(current, "SKILL.md"), frontmatter + "current instructions");
        Assert.Equal("current instructions", new SkillCatalog(store, root, Path.Combine(root, "shared"), root).Get("fixture").Instructions);
    }

    [Fact]
    public void DefaultProjectPathReusesOldDatabaseAndHonorsExplicitAndCurrentPaths()
    {
        var legacy = Path.Combine(root, ".lanagent", "data");
        Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, "lanagent.json"), "fixture");
        var args = TerminalArguments.Parse(["--workspace", root]);
        Assert.Equal(legacy, args.DataDirectory);
        Assert.Equal(root, TerminalArguments.Parse(["--workspace", root, "--data-dir", root]).DataDirectory);
        var current = Path.Combine(root, ".allagent", "data");
        Directory.CreateDirectory(current); File.WriteAllText(Path.Combine(current, "allagent.json"), "fixture");
        Assert.Equal(legacy, args.DataDirectory); // A running launch cannot silently switch storage roots.
        Assert.Equal(current, TerminalArguments.Parse(["--workspace", root]).DataDirectory);
        var explicitArgs = TerminalArguments.Parse(["--workspace", root, "--data-dir", root]);
        Assert.Equal(root, explicitArgs.Options(new(new(false, false))).SharedDirectory);
    }

    [Fact]
    public void SharedDataAndProjectInstructionsKeepLegacyFallbackWithoutMergingRoots()
    {
        var legacy = Path.Combine(root, "LanAgent"); Directory.CreateDirectory(Path.Combine(legacy, "credentials"));
        File.WriteAllText(Path.Combine(legacy, "credentials", "fixture.bin"), "fixture");
        Assert.Equal(legacy, LegacyDataPaths.SharedDirectory(root));
        var current = Path.Combine(root, "AllAgent.NET"); Directory.CreateDirectory(current);
        Assert.Equal(legacy, LegacyDataPaths.SharedDirectory(root));
        File.WriteAllText(Path.Combine(current, "current.setting"), "fixture");
        Assert.Equal(current, LegacyDataPaths.SharedDirectory(root));
        Directory.CreateDirectory(Path.Combine(root, ".lanagent"));
        File.WriteAllText(Path.Combine(root, ".lanagent", "AGENT.md"), "old instruction");
        Assert.Contains("old instruction", ProjectInstructions.Load(root));
        Directory.CreateDirectory(Path.Combine(root, ".allagent"));
        File.WriteAllText(Path.Combine(root, ".allagent", "AGENT.md"), "current instruction");
        Assert.DoesNotContain("old instruction", ProjectInstructions.Load(root));
        Assert.Contains("current instruction", ProjectInstructions.Load(root));
    }

    [Theory]
    [InlineData("[1.1.0,2.0.0)", true)]
    [InlineData("[1.0.0,1.1.0)", false)]
    [InlineData("[2.0.0,)", false)]
    public async Task PluginDependencyChecksActualHostSdk(string range, bool compatible)
    {
        var path = Path.Combine(root, "fixture.nupkg");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(text); }
            Entry("Fixture.nuspec", $"<package><metadata><id>Audit.Fixture</id><version>1.0.0</version><authors>fixture</authors><description>fixture</description><tags>AllAgent.NET</tags><dependencies><group targetFramework=\"net10.0\"><dependency id=\"AllAgent.NET.Core\" version=\"{range}\" /></group></dependencies></metadata></package>");
            Entry("allagent/allagent-plugin.json", "{}");
        }
        var installer = new NuGetPluginInstaller(root);
        if (compatible) Assert.Equal("Audit.Fixture", (await installer.ImportAsync(path)).PackageId);
        else await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(path));
    }

    [Theory]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    public async Task ResponsesRetriesOnlyTransientHttpErrors(int status, int attempts)
    {
        using var store = new DataStore(Path.Combine(root, "data")); var session = new Conversation(); store.AddConversation(session);
        var handler = new StatusHandler((HttpStatusCode)status); using var http = new HttpClient(handler);
        await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        var engine = new AgentEngine(store, new ResponsesProvider(http), code, Options with { MaxModelRetries = 2 });
        var events = await Collect(engine.RunAsync(session.Id, "task", new ApiConfig { ProviderId = "responses", DefaultModel = "fixture", BaseUrl = "https://fixture.invalid/v1" }));
        Assert.Equal(attempts, handler.Calls);
        Assert.Equal(attempts - 1, events.OfType<AgentNotice>().Count());
        Assert.Single(events.OfType<AgentError>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedTextSurvivesReopenWithoutExecutableCallsOrNativeState(bool cancel)
    {
        var directory = Path.Combine(root, "data"); using var store = new DataStore(directory);
        var session = new Conversation(); store.AddConversation(session);
        using var cancellation = new CancellationTokenSource();
        var provider = new PartialProvider(cancel ? cancellation : null);
        await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        var engine = new AgentEngine(store, provider, code, Options);
        var events = await Collect(engine.RunAsync(session.Id, "task", new ApiConfig { DefaultModel = "fixture" }, cancellation.Token));
        Assert.Equal(cancel ? "cancelled" : "error", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        using (var reader = new DataStore(directory))
        {
            var partial = Assert.Single(reader.GetMessages(session.Id), m => m.Role == "assistant");
            Assert.True(partial.IsInterrupted); Assert.Equal("partial", partial.Content); Assert.Equal("thought", partial.ReasoningContent);
            Assert.Null(partial.ToolCallsJson); Assert.Null(partial.ProviderStateJson);
            Assert.Contains("interrupted", SessionTranscript.Format(reader.GetConversation(session.Id)!));
        }
        await Collect(engine.RunAsync(session.Id, "continue", new ApiConfig { DefaultModel = "fixture" }));
        var replayed = Assert.Single(provider.Requests[1].Messages, m => m.Role == "assistant");
        Assert.Contains("interrupted", replayed.Content); Assert.Null(replayed.ToolCallsJson); Assert.Null(replayed.ProviderStateJson);
        var branch = SessionOperations.Branch(store, store.GetConversation(session.Id)!, "terminal compact regression");
        await Collect(engine.CompactAsync(session.Id, new ApiConfig { DefaultModel = "fixture" }, "summarize"));
        Assert.Contains("interrupted", provider.Requests[2].Messages.Last().Content);
        await SessionOperations.CompactAsync(store, branch, new ApiConfig { DefaultModel = "fixture" }, new TerminalUi(true, false), 100_000, "", default, provider);
        Assert.Contains("interrupted", provider.Requests[3].Messages.Last().Content);
    }

    [Fact]
    public async Task AbnormalFinishKeepsVisibleTextAndCannotCommitPartialCode()
    {
        using var store = new DataStore(Path.Combine(root, "data")); var session = new Conversation(); store.AddConversation(session);
        await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        var events = await Collect(new AgentEngine(store, new PartialProvider(null, "content_filter"), code, Options)
            .RunAsync(session.Id, "task", new ApiConfig { DefaultModel = "fixture" }));
        Assert.Single(events.OfType<AgentError>());
        var partial = Assert.Single(store.GetMessages(session.Id), message => message.Role == "assistant");
        Assert.True(partial.IsInterrupted); Assert.Equal("partial", partial.Content); Assert.Null(partial.ToolCallsJson);
        Assert.DoesNotContain(store.GetMessages(session.Id), message => message.Role == "tool");
    }

    [Fact]
    public async Task OversizedInterruptedResponseCannotBypassPersistedOutputBudget()
    {
        using var store = new DataStore(Path.Combine(root, "data")); var session = new Conversation(); store.AddConversation(session);
        await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        const int budget = 100_000; // Leave room for the normal system prompt before exercising response overflow.
        var events = await Collect(new AgentEngine(store, new PartialProvider(null, output: new string('x', budget + 1)), code,
            Options with { MaxContextCharacters = budget }).RunAsync(session.Id, "task", new ApiConfig { DefaultModel = "fixture" }));
        Assert.Single(events.OfType<AgentError>());
        var partial = Assert.Single(store.GetMessages(session.Id), message => message.Role == "assistant");
        Assert.True(partial.IsInterrupted); Assert.Equal(budget, partial.Content.Length);
        Assert.Null(partial.ToolCallsJson); Assert.Null(partial.ProviderStateJson);
    }

    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> stream)
    { var result = new List<AgentEvent>(); await foreach (var item in stream) result.Add(item); return result; }
    private sealed class EmptySandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => throw new InvalidOperationException("Partial calls must never execute.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(status)); }
    }
    private sealed class PartialProvider(CancellationTokenSource? cancellation, string? finishReason = null, string output = "partial") : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield();
            if (Requests.Count > 1) { yield return new(ChunkType.TextDelta, "continued"); yield return new(ChunkType.Done, FinishReason: "stop"); yield break; }
            yield return new(ChunkType.TextDelta, output); yield return new(ChunkType.ThinkingDelta, "thought");
            yield return new(ChunkType.ToolCallDelta, ToolCall: new("unfinished", "execute_code", "{\"code\":"));
            yield return new(ChunkType.ProviderState, ProviderStateJson: "{\"unverified\":true}");
            if (finishReason != null) { yield return new(ChunkType.Done, FinishReason: finishReason); yield break; }
            if (cancellation != null) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new IOException("connection reset");
        }
    }
}
