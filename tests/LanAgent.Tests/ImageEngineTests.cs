using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using Xunit;
using LanAgent.Terminal;
using LanAgent.Core.Tools;
using System.Text.Json;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class ImageEngineTests
{
    [Fact]
    public async Task HostScreenshotIsForwardedAsImageAfterToolResponseAndRetainedInHistory()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "screenshot-data"));
        var session = new Conversation(); store.AddConversation(session);
        var image = ImageAttachment.FromBytes("window.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC"));
        var provider = new ScreenshotProvider(); await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        runtime.Tools.Register(new ScreenshotTool(image));
        var engine = new AgentEngine(store, provider, runtime.CodeAct, workspace.Options);
        await foreach (var evt in engine.RunAsync(session.Id, "Inspect a window screenshot", new ApiConfig { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        var observation = Assert.Single(provider.Requests[1].Messages, m => m.Images.Count > 0);
        Assert.Equal("user", observation.Role); Assert.Equal(image.Base64, Assert.Single(observation.Images).Base64);
        Assert.DoesNotContain(image.Base64, provider.Requests[1].Messages.Single(m => m.Role == "tool").Content);
        Assert.Contains(store.GetMessages(session.Id), m => m.IsInternal && m.Images.Count == 1);
    }
    private sealed class ScreenshotTool(ImageAttachment image) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new("fixture_screenshot", "Screenshot transport fixture", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct) => ValueTask.FromResult(ToolResult.Success(new { imageAttached = true }) with { Images = [image] });
    }
    [Fact]
    public async Task FileReadImageIsForwardedThroughActualCodeAct()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "image-data"));
        var session = new Conversation(); store.AddConversation(session);
        var image = ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC"));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "pixel.png"), Convert.FromBase64String(image.Base64));
        var provider = new ScreenshotProvider("print(call_tool('file_read', path='pixel.png'))");
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, workspace.Options).RunAsync(session.Id, "Inspect image", new ApiConfig { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        var observed = Assert.Single(provider.Requests[1].Messages.SelectMany(m => m.Images));
        Assert.Equal(image.Base64, observed.Base64);
        Assert.DoesNotContain(image.Base64, provider.Requests[1].Messages.Single(m => m.Role == "tool").Content);
    }

    private sealed class ScreenshotProvider(string code = "print(call_tool('fixture_screenshot'))") : IAiProvider
    {
        public string ProviderId => "fixture"; public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new(ChunkType.ToolCallDelta, ToolCall: new("capture", "execute_code", JsonSerializer.Serialize(new { code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
            }
            else { yield return new(ChunkType.TextDelta, TextDelta: "Image inspected"); yield return new(ChunkType.Done, FinishReason: "stop"); }
        }
    }
    [Fact]
    public async Task AgentHistoryRetainsImageBytesOnFollowingTurn()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var image = ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC"));
        var provider = new Provider(); await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var engine = new AgentEngine(store, provider, runtime.CodeAct, workspace.Options);
        var config = new ApiConfig { DefaultModel = "fixture" };
        await foreach (var evt in engine.RunAsync(session.Id, "Describe", config, images: [image], inputText: "Human draft @pixel.png")) Assert.IsNotType<AgentError>(evt);
        Assert.Equal("Human draft @pixel.png", store.GetMessages(session.Id).Single(m => m.Role == "user").InputText);
        await foreach (var evt in engine.RunAsync(session.Id, "Compare again", config)) Assert.IsNotType<AgentError>(evt);
        var restoredImageMessage = Assert.Single(provider.Requests[1].Messages, m => m.Role == "user" && m.Content.Contains("Describe"));
        Assert.Equal(image.Base64, Assert.Single(restoredImageMessage.Images).Base64);
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
