using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class RuntimeModelSwitchTests
{
    [Fact]
    public async Task SwitchAtStepBoundaryRoutesProviderPreservesHistoryAndDropsOldProviderState()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var first = new ApiConfig { ProviderId = "first", DefaultModel = "first-model" };
        var second = new ApiConfig { ProviderId = "second", DefaultModel = "second-model", ModelContextWindows = new() { ["second-model"] = 20000 } };
        var parent = new Conversation(); store.AddConversation(parent);
        var child = new Conversation { ParentConversationId = parent.Id, ModelId = first.DefaultModel, ProviderId = first.ProviderId, SubAgentConfigId = first.Id }; store.AddConversation(child);
        ApiConfig current = first;
        var oldProvider = new Provider(first.ProviderId, () => { store.EnqueueInput(child.Id, "CORRECTION", "CORRECTION", [], "steer", second); current = second; });
        var newProvider = new Provider(second.ProviderId);
        var options = w.Options with { IsSubAgent = true, ConfigurationProvider = (_, _) => SubAgentModelSelection.Resolve(current, [first, second]) };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var engine = new AgentEngine(store, new RequestRoutingAiProvider(c => c.ProviderId == "first" ? oldProvider : newProvider), runtime.CodeAct, options);
        var events = new List<AgentEvent>();
        await foreach (var evt in engine.RunAsync(child.Id, "INITIAL", first)) events.Add(evt);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Equal("first-model", Assert.Single(oldProvider.Requests).Config.DefaultModel);
        var final = Assert.Single(newProvider.Requests); Assert.Equal("second-model", final.Config.DefaultModel);
        Assert.Contains(final.Messages, m => m.Content == "INITIAL"); Assert.Contains(final.Messages, m => m.Content == "CORRECTION");
        Assert.Contains(final.Messages, m => m.Content == "first reply"); Assert.All(final.Messages, m => Assert.Null(m.ProviderStateJson));
        Assert.Equal(second.Id, child.SubAgentConfigId); Assert.Equal(second.DefaultModel, child.ModelId);
        Assert.Equal("first-model", first.DefaultModel); Assert.Empty(store.GetPendingInputs(child.Id));
        Assert.Contains(events.OfType<AgentContextUsage>(), e => e.CapacityTokens == 20000);
        Assert.Equal("execute_code", Assert.Single(final.Tools!).Name);
        using var reopened = new DataStore(Path.Combine(w.Root, "data"));
        var resumed = new AgentEngine(reopened, new RequestRoutingAiProvider(c => c.ProviderId == "first" ? oldProvider : newProvider), runtime.CodeAct, options);
        await foreach (var evt in resumed.RunAsync(child.Id, "CONTINUE", first)) Assert.IsNotType<AgentError>(evt);
        var restored = newProvider.Requests[1];
        Assert.Null(Assert.Single(restored.Messages, m => m.Content == "first reply").ProviderStateJson);
        Assert.Contains("new-provider-response", Assert.Single(restored.Messages, m => m.Content == "second reply").ProviderStateJson);
    }

    [Fact]
    public void InvalidSwitchDoesNotAdmitInputOrChangeChildConfiguration()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var child = new Conversation { ParentConversationId = Guid.NewGuid(), ModelId = "original" }; store.AddConversation(child);
        Assert.Throws<ArgumentException>(() => store.EnqueueInput(child.Id, "invalid", "invalid", [], "steer", new ApiConfig { IsEnabled = false, DefaultModel = "new" }));
        Assert.Empty(store.GetPendingInputs(child.Id)); Assert.Equal("original", child.ModelId);
    }

    private sealed class Provider(string id, Action? switchModel = null) : IAiProvider
    {
        public string ProviderId => id;
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (switchModel != null)
            {
                yield return new(ChunkType.ProviderState, ProviderStateJson: "{\"protocol\":\"responses\",\"output\":[{\"type\":\"reasoning\",\"id\":\"old-provider-response\",\"summary\":[]}]}");
                switchModel();
            }
            else yield return new(ChunkType.ProviderState, ProviderStateJson: "{\"protocol\":\"responses\",\"output\":[{\"type\":\"reasoning\",\"id\":\"new-provider-response\",\"summary\":[]}]}");
            yield return new(ChunkType.TextDelta, TextDelta: id == "first" ? "first reply" : "second reply");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
