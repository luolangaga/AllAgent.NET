using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services.AI;
using LanAgent.Terminal;
using Microsoft.AspNetCore.Http;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class TokenUsageStatsTests
{
    [Fact]
    public async Task CumulativeSnapshotsAreCountedOnceAndModelSwitchesAreSeparate()
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data");
        using (var store = new DataStore(directory))
        {
            var config = new ApiConfig { ProviderId = "one", DefaultModel = "model-a" };
            var provider = new UsageTrackingAiProvider(new FixtureProvider([new(100, 0), new(100, 25), new(100, 25)]), store);
            await Consume(provider, config);
            config.DefaultModel = "model-b"; await Consume(provider, config);
            config.ProviderId = "two"; await Consume(provider, config);
            config.ProviderId = "one"; config.DefaultModel = "model-a"; await Consume(provider, config);
            var first = store.GetTokenUsageStats().First();
            Assert.Equal(new ModelUsageStats("one", "model-a", 200, 50, 2, 2), first);
            // Copying/deleting history must not change actual spending.
            var original = SessionOperations.Get(store, null);
            SessionOperations.Branch(store, original, "copy"); store.DeleteConversation(original.Id);
            Assert.Equal(3, store.GetTokenUsageStats().Count);
        }
        using var reloaded = new DataStore(directory);
        Assert.Equal(500, reloaded.GetTokenUsageStats().Sum(s => s.TotalTokens));
        Assert.Equal(4, reloaded.GetTokenUsageStats().Sum(s => s.Requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRequestsKeepObservedUsage(bool cancel)
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        using var source = new CancellationTokenSource();
        var provider = new UsageTrackingAiProvider(new FixtureProvider([new(10, 3)], fail: !cancel), store);
        var request = new ChatRequest(new ApiConfig { ProviderId = "one", DefaultModel = "model" }, [new("user", "hi")]);
        if (cancel)
        {
            await using var iterator = provider.StreamChatAsync(request, source.Token).GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync()); source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        }
        else await Assert.ThrowsAsync<IOException>(() => Consume(provider, request.Config));
        Assert.Equal(new ModelUsageStats("one", "model", 10, 3, 1, 1), Assert.Single(store.GetTokenUsageStats()));
    }

    [Fact]
    public async Task MissingUsageAndEarlyDisposalAreRecordedWithoutEstimates()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var config = new ApiConfig { ProviderId = "one", DefaultModel = "model" };
        await Consume(new UsageTrackingAiProvider(new FixtureProvider([]), store), config);
        await foreach (var chunk in new UsageTrackingAiProvider(new FixtureProvider([new(10, 4), new(20, 8)]), store)
            .StreamChatAsync(new(config, [new("user", "hi")]))) break;
        var stats = Assert.Single(store.GetTokenUsageStats());
        Assert.Equal(14, stats.TotalTokens); Assert.Equal(2, stats.Requests); Assert.Equal(1, stats.RequestsWithoutUsage);
    }

    [Fact]
    public async Task ConcurrentRequestsAccumulateBeyondIntRangeAndOldStoresRemainReadable()
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "lanagent.json"), """{"version":2,"conversations":[],"tokenUsageStats":null}""");
        using var store = new DataStore(directory); Assert.Empty(store.GetTokenUsageStats());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.RecordTokenUsage("one", "model", new(int.MaxValue, 2)))));
        var stats = Assert.Single(store.GetTokenUsageStats());
        Assert.Equal(8L * int.MaxValue, stats.InputTokens); Assert.Equal(16, stats.OutputTokens); Assert.Equal(8, stats.Requests);
    }

    [Fact]
    public async Task ManualCompactionCountsUsageWithoutDuplicatingArchivedHistory()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var conversation = SessionOperations.Get(store, null);
        store.AddMessage(conversation.Id, new Message { ConversationId = conversation.Id, Role = "user", Content = "task" });
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"summary\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":12}}\n\ndata: [DONE]\n\n");
        });
        var config = new ApiConfig { ProviderId = "openai", DefaultModel = "model", BaseUrl = server.Url };
        await SessionOperations.CompactAsync(store, conversation, config, new TerminalUi(true, false), 32000, "", default);
        Assert.Equal(2, store.Conversations.Count);
        Assert.Equal(new ModelUsageStats("openai", "model", 100, 12, 1, 1), Assert.Single(store.GetTokenUsageStats()));
    }

    [Fact]
    public async Task StatsCliOutputsJsonForEmptyAndPersistedStoresWithoutModelSetup()
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data");
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var exe = Path.GetFullPath($"../../../../../src/LanAgent.Terminal/bin/{configuration}/net8.0/lanagent.exe", AppContext.BaseDirectory);
        async Task<JsonElement> Stats()
        {
            var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(exe,
                ["stats", "--json", "--workspace", w.Root, "--data-dir", directory]), TimeSpan.FromSeconds(30), default);
            Assert.Equal(0, result.ExitCode); Assert.Empty(result.Stderr);
            return JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        }
        var empty = await Stats(); Assert.Empty(empty.GetProperty("models").EnumerateArray());
        Assert.Equal(0, empty.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        using (var store = new DataStore(directory))
        {
            store.RecordTokenUsage("one", "model-a", new(100, 20));
            store.RecordTokenUsage("one", "model-a", null);
            store.RecordTokenUsage("two", "model-b", new(40, 10));
        }
        var json = await Stats(); Assert.Equal(2, json.GetProperty("models").GetArrayLength());
        var totals = json.GetProperty("totals"); Assert.Equal(170, totals.GetProperty("totalTokens").GetInt64());
        Assert.Equal(3, totals.GetProperty("requests").GetInt64()); Assert.Equal(1, totals.GetProperty("requestsWithoutUsage").GetInt64());
    }

    [Fact]
    public void StatsTableRendersTotalsAndEscapesModelNames()
    {
        var console = new TestConsole(); console.Profile.Width = 160;
        console.Write(UsageStatistics.Render([new("provider", "model[red]", 120, 30, 2, 1), new("other", "model-b", 20, 10, 1, 1)]));
        Assert.Contains("model[red]", console.Output); Assert.Contains("输入 Token", console.Output);
        Assert.Contains("输出 Token", console.Output); Assert.Contains("未返回用量", console.Output); Assert.Contains("合计", console.Output);
        Assert.Contains("180", console.Output);
        Assert.Contains(CommandCatalog.Builtins, c => c.Text == "/stats");
    }

    private static async Task Consume(IAiProvider provider, ApiConfig config)
    { await foreach (var chunk in provider.StreamChatAsync(new(config, [new("user", "hi")]))) { } }

    private sealed class FixtureProvider(ModelTokenUsage[] snapshots, bool fail = false) : IAiProvider
    {
        public string ProviderId => "fixture";
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var snapshot in snapshots) { ct.ThrowIfCancellationRequested(); yield return new(ChunkType.Usage) { Usage = snapshot }; }
            ct.ThrowIfCancellationRequested();
            if (fail) throw new IOException("disconnected");
            yield return new(ChunkType.Done);
        }
    }
}
