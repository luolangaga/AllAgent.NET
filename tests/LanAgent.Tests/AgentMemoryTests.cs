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
    private static readonly ApiConfig Config = new() { ProviderId = "fixture", DefaultModel = "fixture", IsEnabled = true };

    [Fact]
    public async Task ExistingAuthorProfileIsIgnoredAndNeverUpdated()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Author);
        var path = Path.Combine(fixture.Author, "AUTHOR.md");
        const string original = "PRIVATE-AUTHOR-PROFILE";
        File.WriteAllText(path, original);
        using var store = new DataStore(Path.Combine(fixture.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new Sandbox());
        var provider = new Provider(_ => "answer");
        var engine = new AgentEngine(store, provider, codeAct, new AgentOptions { WorkspaceRoot = fixture.Root, SharedDirectory = fixture.Author });
        var events = new List<AgentEvent>();
        await foreach (var evt in engine.RunAsync(conversation.Id, "我喜欢 .NET，请记住我的偏好", Config)) events.Add(evt);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(provider.Requests[0].Messages, m => m.Content.Contains(original));
        Assert.DoesNotContain(events, e => e is ToolCallStarted);
        Assert.DoesNotContain(store.GetMessages(conversation.Id), m => m.IsHostActivity);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(fixture.Author));
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
