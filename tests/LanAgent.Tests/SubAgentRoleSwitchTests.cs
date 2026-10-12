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

[Collection("Hyperlight acceptance")]
public sealed class SubAgentRoleSwitchTests
{
    [Fact]
    public async Task RealMicroVmRoleSwitchBlocksGeneratedWriteAndGeneralRestoresParentGrant()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var child = new Conversation { ParentConversationId = Guid.NewGuid(), AgentRole = "general" }; store.AddConversation(child);
        File.WriteAllText(Path.Combine(w.Root, "safe.txt"), "READ-PROOF");
        var policy = new SubAgentRolePolicy(() => child.AgentRole == "explore", w.Root);
        var options = w.Options with { IsSubAgent = true, FullAccess = true, InvocationPolicy = policy.Validate, InstructionsProvider = () => "CURRENT ROLE: " + child.AgentRole };
        await using var runtime = await AgentRuntime.CreateAsync(options); var provider = new Provider(store, child.Id);
        var engine = new AgentEngine(store, provider, runtime.CodeAct, options); var config = new ApiConfig { DefaultModel = "fixture" };
        await foreach (var evt in engine.RunAsync(child.Id, "INITIAL", config)) Assert.IsNotType<AgentError>(evt);
        Assert.False(File.Exists(Path.Combine(w.Root, "write.txt")));
        Assert.Contains(provider.Requests[1].Messages, m => m.Role == "system" && m.Content == "You are a helpful agent.\n\nCURRENT ROLE: explore");
        Assert.Contains(store.GetMessages(child.Id), m => m.Role == "tool" && m.Content.Contains("read-only") && m.Content.Contains("READ-PROOF"));
        store.EnqueueInput(child.Id, "IMPLEMENT", "IMPLEMENT", [], "steer", subAgentRole: "general");
        await foreach (var evt in engine.RunAsync(child.Id, "continue", config, fromInbox: true)) Assert.IsNotType<AgentError>(evt);
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(w.Root, "write.txt")));
        Assert.Contains(provider.Requests[2].Messages, m => m.Role == "system" && m.Content.Contains("CURRENT ROLE: general"));
        Assert.Equal(4, provider.Requests.Count); Assert.Empty(store.GetPendingInputs(child.Id));
        Assert.All(provider.Requests, request => Assert.Equal("execute_code", Assert.Single(request.Tools!).Name));
    }

    [Fact]
    public async Task RoleChangedWhileAwaitingApprovalIsRecheckedBeforeInvocation()
    {
        using var w = new TestWorkspace(); var readOnly = false;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new SubAgentRolePolicy(() => readOnly, w.Root);
        var registry = new ToolRegistry(async (_, ct) => { waiting.SetResult(); return await approved.Task.WaitAsync(ct); }, invocationPolicy: policy.Validate);
        registry.Register(w.Tools);
        var call = registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = "revoked.txt", content = "forbidden" }), CancellationToken.None).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); readOnly = true; approved.SetResult(true);
        var result = await call; Assert.False(result.Ok); Assert.Contains("read-only", result.Error); Assert.False(File.Exists(Path.Combine(w.Root, "revoked.txt")));
    }

    [Fact]
    public async Task ParentReadOnlyRestrictionStillBlocksGeneralAndExternalReadUnderFullAccess()
    {
        using var w = new TestWorkspace(); using var outside = new TestWorkspace();
        File.WriteAllText(Path.Combine(outside.Root, "outside.txt"), "outside");
        var permissions = new ExternalDirectoryPermissions();
        var policy = new SubAgentRolePolicy(() => true, w.Root, permissions);
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with { FullAccess = true, InvocationPolicy = policy.Validate, ExternalDirectoryPermissions = permissions });
        var write = await runtime.Tools.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = "forbidden.txt", content = "forbidden" }), CancellationToken.None);
        var read = await runtime.Tools.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = Path.Combine(outside.Root, "outside.txt") }), CancellationToken.None);
        Assert.False(write.Ok); Assert.False(read.Ok); Assert.False(File.Exists(Path.Combine(w.Root, "forbidden.txt")));
        permissions.Allow(Guid.Empty, outside.Root);
        var authorizedRead = await runtime.Tools.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = Path.Combine(outside.Root, "outside.txt") }), CancellationToken.None);
        Assert.True(authorizedRead.Ok, authorizedRead.Error);
    }

    private sealed class Provider(DataStore store, Guid child) : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() }); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (Requests.Count is 1 or 3)
            {
                if (Requests.Count == 1) store.EnqueueInput(child, "SEARCH", "SEARCH", [], "steer", subAgentRole: "explore");
                var code = "print(call_tool(\"file_write\", path=\"write.txt\", content=\"allowed\"))\nprint(call_tool(\"file_read\", path=\"safe.txt\"))";
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("role-" + Requests.Count, "execute_code", JsonSerializer.Serialize(new { code })));
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
            }
            else { yield return new(ChunkType.TextDelta, TextDelta: "done"); yield return new(ChunkType.Done, FinishReason: "stop"); }
        }
    }
}
