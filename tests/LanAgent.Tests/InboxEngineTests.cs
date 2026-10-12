using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class InboxEngineTests
{
    [Fact]
    public async Task SteerArrivingDuringResponseResumesBeforeStopAndQueueWaitsForNextRun()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var queued = store.EnqueueInput(session.Id, "Later task", "QUEUED_AFTER_TASK", []);
        var provider = new Provider(store, session.Id); await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var engine = new AgentEngine(store, provider, runtime.CodeAct, workspace.Options); var config = new ApiConfig { DefaultModel = "fixture" };
        var events = new List<AgentEvent>();
        await foreach (var evt in engine.RunAsync(session.Id, "Start task", config)) events.Add(evt);
        Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(provider.Requests[0].Messages, m => m.Content == "CORRECTION_WITH_IMAGE");
        var correction = Assert.Single(provider.Requests[1].Messages, m => m.Content == "CORRECTION_WITH_IMAGE");
        Assert.Equal(InputInboxTests.Png, Assert.Single(correction.Images).Base64);
        Assert.All(provider.Requests, request => Assert.DoesNotContain(request.Messages, m => m.Content == "QUEUED_AFTER_TASK"));
        Assert.Equal(queued.Id, Assert.Single(store.GetPendingInputs(session.Id)).Id);
        Assert.Single(events.OfType<AgentInputAdmitted>());
        await foreach (var evt in engine.RunAsync(session.Id, queued.Content, config, fromInbox: true)) Assert.IsNotType<AgentError>(evt);
        Assert.Single(provider.Requests[2].Messages, m => m.Content == queued.Content);
        Assert.Single(store.GetMessages(session.Id), m => m.Id == queued.Id);
        Assert.Empty(store.GetPendingInputs(session.Id));
    }
    [Fact]
    public async Task InvalidConfigDoesNotConsumeDurableInput()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session); var queued = store.EnqueueInput(session.Id, "Later", "Later", []);
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var engine = new AgentEngine(store, new Provider(store, session.Id), runtime.CodeAct, workspace.Options);
        await foreach (var _ in engine.RunAsync(session.Id, queued.Content, new ApiConfig { DefaultModel = "" }, fromInbox: true)) { }
        Assert.Equal(queued.Id, Assert.Single(store.GetPendingInputs(session.Id)).Id); Assert.Empty(store.GetMessages(session.Id));
    }
    private sealed class Provider(DataStore store, Guid session) : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (Requests.Count == 1) store.EnqueueInput(session, "Correction", "CORRECTION_WITH_IMAGE", [ImageAttachment.FromBytes("correction.png", Convert.FromBase64String(InputInboxTests.Png))], "steer");
            yield return new(ChunkType.TextDelta, TextDelta: "Fixture result"); yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
