using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using Xunit;
using LanAgent.Terminal;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class ImageEngineTests
{
    [Fact]
    public async Task AgentHistoryRetainsImageBytesOnFollowingTurn()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var image = ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII="));
        var provider = new Provider(); await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var engine = new AgentEngine(store, provider, runtime.CodeAct, workspace.Options);
        var config = new ApiConfig { DefaultModel = "fixture" };
        await foreach (var evt in engine.RunAsync(session.Id, "Describe", config, images: [image], inputText: "Human draft @pixel.png")) Assert.IsNotType<AgentError>(evt);
        Assert.Equal("Human draft @pixel.png", store.GetMessages(session.Id).Single(m => m.Role == "user").InputText);
        await foreach (var evt in engine.RunAsync(session.Id, "Compare again", config)) Assert.IsNotType<AgentError>(evt);
        Assert.Equal(image.Base64, Assert.Single(provider.Requests[1].Messages.Single(m => m.Content == "Describe").Images).Base64);
        Assert.Equal("execute_code", Assert.Single(provider.Requests[1].Tools!).Name);
        session.ContextSummary = "BRANCH-SUMMARY-PROOF";
        session.ContextSummaryThroughId = store.GetMessages(session.Id).First(m => m.Role == "user").Id;
        store.UpdateConversation(session);
        var fork = SessionOperations.Branch(store, session, "branch fixture");
        await foreach (var evt in engine.RunAsync(fork.Id, "Resume branch", config)) Assert.IsNotType<AgentError>(evt);
        Assert.Contains(provider.Requests[2].Messages, m => m.Content.Contains("BRANCH-SUMMARY-PROOF"));
        Assert.DoesNotContain(provider.Requests[2].Messages, m => m.Content == "Describe");
    }
    private sealed class Provider : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new(ChunkType.TextDelta, TextDelta: "Image received"); yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
