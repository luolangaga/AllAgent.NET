using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class MessageRevertEngineTests
{
    [Fact]
    public async Task NewPreparedUserTurnCommitsStageWhileInvalidAndInternalTurnsRetainIt()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(w.Root), Path.Combine(w.Root, "data"));
        var (session, users) = MessageRevertTests.History(store, checkpoints, w.Root);
        checkpoints.RevertRange(store, session.Id, users[1].Id);
        var options = w.Options with { Checkpoints = checkpoints };
        await using var runtime = await AgentRuntime.CreateAsync(options); var provider = new Provider();
        var engine = new AgentEngine(store, provider, runtime.CodeAct, options);
        await foreach (var _ in engine.RunAsync(session.Id, "invalid", new ApiConfig { DefaultModel = "" })) { }
        await foreach (var _ in engine.RunAsync(session.Id, "internal notification", new ApiConfig { DefaultModel = "fixture" }, internalMessage: true)) { }
        Assert.NotNull(session.Revert); Assert.Empty(provider.Requests); Assert.Equal(7, store.GetMessages(session.Id).Count);
        var next = checkpoints.Begin(session.Id, "new goal", store.GetVisibleMessages(session.Id));
        await foreach (var evt in engine.RunAsync(session.Id, "NEW-GOAL-PROOF", new ApiConfig { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        checkpoints.End(store.GetVisibleMessages(session.Id));
        Assert.Null(session.Revert); Assert.Equal(2, session.InvalidatedCheckpoints.Count);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Content == "task 1"); Assert.Contains(request.Messages, m => m.Content == "NEW-GOAL-PROOF");
        Assert.DoesNotContain(request.Messages, m => m.Content is "task 2" or "task 3");
        Assert.Equal("execute_code", Assert.Single(request.Tools!).Name);
        Assert.Equal(3, checkpoints.List(session.Id).Single(c => c.Id == next.Id).BeforeMessages.Count);
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(w.Root, "shared.txt")));
    }
    private sealed class Provider : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new(ChunkType.TextDelta, TextDelta: "new response"); yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
