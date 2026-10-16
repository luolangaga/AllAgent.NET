using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class SubAgentExecutionSettingsTests
{
    [Fact]
    public void OverridesCloneModelAndNarrowParentLimits()
    {
        var model = new ApiConfig { DefaultModel = "fixture", Temperature = .7, MaxTokens = 100 };
        var settings = new SubAgentExecutionSettings { Temperature = .2, MaxOutputTokens = 200, MaxToolCalls = 10, TimeoutSeconds = 30, MaxContextCharacters = 16000 };
        var selected = settings.Apply(model);
        Assert.Equal(.7, model.Temperature); Assert.Equal(.2, selected.Temperature); Assert.Equal(100, selected.MaxTokens);
        var options = settings.Apply(new AgentOptions { MaxToolCalls = 3, RunTimeout = TimeSpan.FromSeconds(10), MaxContextCharacters = 8000 });
        Assert.Equal(3, options.MaxToolCalls); Assert.Equal(TimeSpan.FromSeconds(10), options.RunTimeout); Assert.Equal(8000, options.MaxContextCharacters);
        Assert.Equal(0, options.MaxModelRetries);
    }

    [Fact]
    public void InvalidLimitsAndUnpricedCostAreRejected()
    {
        foreach (var settings in new[] { new SubAgentExecutionSettings { Temperature = double.NaN }, new() { MaxCost = 1 }, new() { MaxConcurrent = 0 }, new() { MaxConcurrent = 7 }, new() { MaxDepth = 2 }, new() { FailurePolicy = "fallback" }, new() { ThinkingBudget = 1000 } })
            Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void RoleSettingsRoundTripThroughCatalog()
    {
        using var store = new DataStore(Path.Combine(Path.GetTempPath(), "LanAgent-role-" + Guid.NewGuid().ToString("N")));
        var role = new AgentRoleDefinition("budget", "Budget role", "Review only", Execution: new() { MaxTotalTokens = 10000, Skills = ["verify"] });
        new AgentRoleCatalog(store).Import(JsonSerializer.Serialize(new[] { role }));
        Assert.Equal(10000, new AgentRoleCatalog(store).Get("budget").Execution!.MaxTotalTokens);
    }

    [Fact]
    public async Task ConcurrencyWaitCanBeCancelledAndLeaseIsReleased()
    {
        using var store = new DataStore(Path.Combine(Path.GetTempPath(), "LanAgent-role-" + Guid.NewGuid().ToString("N")));
        var first = await SubAgentConcurrency.AcquireAsync(store, "review", 1, default);
        using var ct = new CancellationTokenSource();
        var pending = SubAgentConcurrency.AcquireAsync(store, "review", 1, ct.Token);
        Assert.False(pending.IsCompleted); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        first.Dispose(); first.Dispose();
        using var next = await SubAgentConcurrency.AcquireAsync(store, "review", 1, default);
    }

    [Fact]
    public async Task AllRolesShareSixSlotsAndReleaseAllowsQueuedWork()
    {
        Assert.Equal(6, new SubAgentExecutionSettings().MaxConcurrent);
        using var store = new DataStore(Path.Combine(Path.GetTempPath(), "LanAgent-role-" + Guid.NewGuid().ToString("N")));
        var leases = new List<IDisposable>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            for (var i = 0; i < 6; i++)
                leases.Add(await SubAgentConcurrency.AcquireAsync(store, "role" + i, 6, timeout.Token));
            var pending = SubAgentConcurrency.AcquireAsync(store, "seventh", 6, timeout.Token);
            Assert.False(pending.IsCompleted);
            leases[0].Dispose();
            using var admitted = await pending;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task TokenBudgetAccumulatesAcrossRequestsAndBlocksBeforeNextCall()
    {
        var inner = new FixtureProvider();
        var provider = new SubAgentAiProvider(inner, new() { MaxTotalTokens = 15 });
        var request = new ChatRequest(new ApiConfig { DefaultModel = "fixture" }, [new("user", "1234567890")]);
        await foreach (var _ in provider.StreamChatAsync(request)) { }
        await Assert.ThrowsAsync<SubAgentBudgetExceededException>(async () => { await foreach (var _ in provider.StreamChatAsync(request)) { } });
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task CostBudgetStopsOutput()
    {
        var provider = new SubAgentAiProvider(new FixtureProvider(), new() { MaxCost = .000010m, InputCostPerMillion = 1, OutputCostPerMillion = 1 });
        await Assert.ThrowsAsync<SubAgentBudgetExceededException>(async () => { await foreach (var _ in provider.StreamChatAsync(new(new ApiConfig { DefaultModel = "fixture" }, [new("user", "1234567890")]))) { } });
    }

    [Fact]
    public async Task FallbackChangesModelWithoutNativeStateAndOnlyOnce()
    {
        var inner = new FixtureProvider { FailFirst = true };
        var provider = new SubAgentAiProvider(inner, new() { FailurePolicy = "fallback", FallbackModel = "backup" }, current => SubAgentModelSelection.Resolve(current, [current], "backup"));
        var request = new ChatRequest(new ApiConfig { DefaultModel = "primary" }, [new("user", "x", ProviderStateJson: "secret-state")]);
        await foreach (var _ in provider.StreamChatAsync(request)) { }
        Assert.Equal("backup", inner.Last!.Config.DefaultModel); Assert.Null(inner.Last.Messages[0].ProviderStateJson);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void SkillAndParentToolAllowListsAreEnforced()
    {
        var role = new AgentRoleDefinition("custom", "Custom", "Task", Execution: new() { Skills = ["verify"] });
        var policy = new SubAgentRolePolicy(() => false, Environment.CurrentDirectory, role: () => role, parentTools: () => ["skill_read"]);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        Assert.NotNull(policy.Validate(new("skill_read", "", schema, ToolRisk.Read), JsonSerializer.SerializeToElement(new { name = "debug" })));
        Assert.Null(policy.Validate(new("skill_read", "", schema, ToolRisk.Read), JsonSerializer.SerializeToElement(new { name = "verify" })));
        Assert.NotNull(policy.Validate(new("file_write", "", schema, ToolRisk.Write), JsonSerializer.SerializeToElement(new { path = "x" })));
    }

    private sealed class FixtureProvider : IAiProvider
    {
        public string ProviderId => "fixture";
        public int Calls; public bool FailFirst; public ChatRequest? Last;
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++; Last = request;
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (FailFirst && Calls == 1) throw new HttpRequestException("offline");
            yield return new(ChunkType.TextDelta, "ok");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }
}
