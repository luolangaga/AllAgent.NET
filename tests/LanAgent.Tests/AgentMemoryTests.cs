using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentMemoryTests
{
    private static readonly ApiConfig Config = new() { DefaultModel = "memory-fixture" };
    private static string Profile(string fact) => AgentMemory.AuthorTemplate + "\n- " + fact;
    private static Message[] Evidence => [new() { Role = "user", Content = "我喜欢 .NET" }];

    [Fact]
    public async Task OnlyAuthorProfileIsInjectedAndUpdatedEvenIfProjectMemoryExists()
    {
        using var fixture = new Fixture(); using var store = new DataStore(Path.Combine(fixture.Root, "data"));
        var projectPath = Path.Combine(fixture.Root, ".lanagent", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "REMOVED-PROJECT-MEMORY");
        var conversation = new Conversation(); store.AddConversation(conversation);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new Sandbox());
        var provider = new Provider(request => request.Tools?.Count > 0 ? "answer" : Profile("prefers .NET"));
        var engine = new AgentEngine(store, provider, codeAct, new AgentOptions { WorkspaceRoot = fixture.Root, AuthorProfileDirectory = fixture.Author });
        var events = new List<AgentEvent>();
        await foreach (var evt in engine.RunAsync(conversation.Id, "我喜欢 .NET", Config)) events.Add(evt);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(3, provider.Requests[0].Messages.Count(m => m.Role == "system"));
        Assert.DoesNotContain(provider.Requests[0].Messages, m => m.Content.Contains("REMOVED-PROJECT-MEMORY"));
        Assert.Equal("author_profile_update", Assert.Single(events.OfType<ToolCallStarted>()).ToolName);
        Assert.Contains("prefers .NET", AgentMemory.Load(fixture.Author));
        Assert.Equal("REMOVED-PROJECT-MEMORY", File.ReadAllText(projectPath));
        File.Delete(projectPath);
        await foreach (var evt in engine.RunAsync(conversation.Id, "next", Config)) { }
        Assert.False(File.Exists(projectPath));
        Assert.Equal(3, provider.Requests.Count); // The ordinary next turn has no profile request.
    }

    [Fact]
    public async Task ProfileRetainsItsLengthLimitAndNoChangeBehavior()
    {
        using var fixture = new Fixture();
        Assert.True(await AgentMemory.UpdateAsync(fixture.Author, new Provider(_ => Profile("prefers .NET")), Config, Evidence, "stop", Guid.NewGuid(), default));
        var previous = AgentMemory.Load(fixture.Author);
        Assert.False(await AgentMemory.UpdateAsync(fixture.Author, new Provider(_ => "NO_CHANGE"), Config, Evidence, "stop", Guid.NewGuid(), default));
        await Assert.ThrowsAsync<IOException>(() => AgentMemory.UpdateAsync(fixture.Author, new Provider(_ => new string('x', 2100)), Config, Evidence, "stop", Guid.NewGuid(), default));
        Assert.Equal(previous, AgentMemory.Load(fixture.Author));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChildrenAndInternalNotificationsDoNotLearn(bool child, bool internalMessage)
    {
        using var fixture = new Fixture(); using var store = new DataStore(Path.Combine(fixture.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new Sandbox());
        var provider = new Provider(_ => "answer");
        var engine = new AgentEngine(store, provider, codeAct, new AgentOptions { WorkspaceRoot = fixture.Root, AuthorProfileDirectory = fixture.Author, IsSubAgent = child });
        await foreach (var evt in engine.RunAsync(conversation.Id, "hi", Config, internalMessage: internalMessage)) Assert.IsNotType<ToolCallStarted>(evt);
        Assert.Single(provider.Requests); Assert.DoesNotContain(store.GetMessages(conversation.Id), m => m.IsHostActivity);
    }

    [Fact]
    public async Task LearningFailureIsVisibleButDoesNotFailTheAnswer()
    {
        using var fixture = new Fixture(); using var store = new DataStore(Path.Combine(fixture.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new Sandbox());
        var provider = new Provider(request => request.Tools?.Count > 0 ? "answer" : "invalid markdown");
        var engine = new AgentEngine(store, provider, codeAct, new AgentOptions { WorkspaceRoot = fixture.Root, AuthorProfileDirectory = fixture.Author });
        var events = new List<AgentEvent>(); await foreach (var evt in engine.RunAsync(conversation.Id, "我喜欢 .NET", Config)) events.Add(evt);
        Assert.Equal("stop", Assert.IsType<AgentDone>(events[^1]).FinishReason); Assert.DoesNotContain(events, evt => evt is AgentError);
        Assert.All(events.OfType<ToolCallCompleted>(), evt => Assert.False(JsonNode.Parse(evt.ResultJson)!["ok"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task RestoringTerminalShowsLearnedFactsFromHostActivity()
    {
        using var fixture = new Fixture(); using var store = new DataStore(Path.Combine(fixture.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var trace = new HostToolTrace("learn", "memory_update", "{}", ToolResult.Success(new { summary = "已更新", learned = new[] { "偏好 .NET" } }).ToJson());
        store.AddMessage(conversation.Id, new Message { ConversationId = conversation.Id, Role = "assistant", IsHostActivity = true, HostToolTraceJson = JsonSerializer.Serialize(new[] { trace }, ToolResult.JsonOptions) });
        var catalog = new SkillCatalog(store, fixture.Root, fixture.Root, fixture.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new(false, false), console);
        screen.Session(conversation.Id, "fixture"); console.Write(screen.BuildFrame(100, 35));
        Assert.Contains("学习本轮经验", console.Output); Assert.Contains("偏好 .NET", console.Output);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LanAgent.MemoryTests", Guid.NewGuid().ToString("N"));
        public string Author => Path.Combine(Root, "author");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "LanAgent.MemoryTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, true); }
    }

    private sealed class Provider(Func<ChatRequest, string> answer, string? finish = "stop", Func<Task>? before = null, bool toolFirst = false) : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            if (before != null) await before(); else await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (toolFirst && Requests.Count == 1)
            {
                yield return new(ChunkType.ToolCallComplete, ToolCall: new("fixture-call", "execute_code", "{\"code\":\"print(1)\"}"));
                yield return new(ChunkType.Done, FinishReason: "tool_calls"); yield break;
            }
            yield return new(ChunkType.TextDelta, TextDelta: answer(request));
            if (finish != null) yield return new(ChunkType.Done, FinishReason: finish);
        }
    }
    private sealed class Sandbox(Action? execute = null) : ICodeSandbox
    {
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) => Task.FromResult<CodeSandboxContext?>(new("fixture", "fixture"));
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) { execute?.Invoke(); return Task.FromResult(ToolResult.Success("unused")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
