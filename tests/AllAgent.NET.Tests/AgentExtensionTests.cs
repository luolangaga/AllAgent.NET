using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class AgentExtensionTests
{
    private static InstructionSkill Skill(string name, string content = "original") => new(name, "description", content, "", "plugin", true, false, true);
    private static AgentTurnContext Turn(IEnumerable<string>? tools = null, IEnumerable<InstructionSkill>? skills = null) =>
        new(Guid.NewGuid(), Environment.CurrentDirectory, false, "task", "original", tools ?? [], skills ?? []);

    [Fact]
    public async Task OrderingCopiesInputsAndRejectsUnknownTools()
    {
        var original = Turn(["read", "write"]);
        var pipeline = new AgentExtensionPipeline(() => [
            new("later", (c, _) => { c.SystemPrompt += "B"; return ValueTask.CompletedTask; }, 10),
            new("earlier", (c, _) => { c.SystemPrompt += "A"; c.Tools.Remove("write"); return ValueTask.CompletedTask; })], () => []);
        var configured = await pipeline.ConfigureTurnAsync(original, default);
        Assert.Equal("originalAB", configured.SystemPrompt); Assert.DoesNotContain("write", configured.Tools);
        Assert.Equal("original", original.SystemPrompt); Assert.Contains("write", original.Tools);
        var invalid = new AgentExtensionPipeline(() => [new("bad", (c, _) => { c.Tools.Add("invented"); return ValueTask.CompletedTask; })], () => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ConfigureTurnAsync(original, default));
    }

    [Fact]
    public async Task ScopedSelectionBlocksCallsAndReferencesAndDoesNotLeakAcrossConcurrentTurns()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        catalog.Create("original", "description", "instructions");
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true)); registry.Register(w.Tools); registry.Register(new SkillTools(catalog));
        var selected = Turn(registry.Definitions.Select(d => d.Name), [Skill("replacement", "modified")]); selected.Tools.Remove("file_write");
        await Task.WhenAll(Task.Run(async () =>
        {
            using var scope = new AgentExtensionScope(selected, registry, catalog);
            await Task.Yield();
            Assert.DoesNotContain(registry.Definitions, d => d.Name == "file_write");
            Assert.False((await registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = "blocked", content = "bad" }), default)).Ok);
            Assert.Equal("replacement", Assert.Single(catalog.List()).Name);
            Assert.Equal("replacement", Assert.Single(catalog.CachedList).Name);
            Assert.Throws<ArgumentException>(() => catalog.ReadReference("original", "SKILL.md"));
            Assert.Throws<ArgumentException>(() => catalog.ReadReference("replacement", "SKILL.md"));
            Assert.Contains("modified", (await registry.CallAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "replacement" }), default)).ToJson());
        }), Task.Run(async () =>
        {
            await Task.Yield();
            Assert.Contains(registry.Definitions, d => d.Name == "file_write");
            Assert.Equal("original", Assert.Single(catalog.List(), s => s.Scope != "builtin").Name);
        }));
        Assert.False(File.Exists(Path.Combine(w.Root, "blocked")));
        Assert.Equal("original", Assert.Single(catalog.List(), s => s.Scope != "builtin").Name);
    }

    [Fact]
    public async Task SynchronousTimeoutCannotCommitLateMutationsAndCancellationPropagates()
    {
        using var release = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = Turn();
        var pipeline = new AgentExtensionPipeline(() => [new("stall", (c, token) =>
        {
            try
            {
                release.Wait();
                Assert.True(token.WaitHandle.WaitOne(0)); // A late callback still owns a valid, cancelled token.
                c.SystemPrompt = "late"; completed.SetResult(); return ValueTask.CompletedTask;
            }
            catch (Exception error) { completed.TrySetException(error); throw; }
        })], () => []) { Timeout = TimeSpan.FromMilliseconds(60) };
        try { await Assert.ThrowsAsync<TimeoutException>(() => pipeline.ConfigureTurnAsync(original, default)); }
        finally { release.Set(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal("original", original.SystemPrompt);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = new AgentExtensionPipeline(() => [new("cancel", (_, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; })], () => []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ConfigureTurnAsync(original, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineUsesPromptAndSkillChangesAndDisableRestoresNextTurn(bool child)
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        var conversation = new Conversation { SystemPrompt = "base" }; store.AddConversation(conversation);
        var registry = new ToolRegistry(); registry.Register(w.Tools); registry.Register(new SkillTools(catalog));
        var enabled = true; var requestCalls = 0;
        var pipeline = new AgentExtensionPipeline(() => enabled ? [new("turn", (c, _) =>
        {
            Assert.Equal(child, c.IsSubAgent); c.SystemPrompt = "replaced"; c.Tools.Remove("file_write");
            c.Skills["injected"] = Skill("injected"); return ValueTask.CompletedTask;
        })] : [], () => enabled ? [new("request", (c, _) =>
        { requestCalls++; c.Messages.Add(new("system", "request-context")); return ValueTask.CompletedTask; })] : []);
        var sandbox = new Sandbox();
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator(w.Python), sandbox);
        var provider = new Provider();
        var options = w.Options with { Extensions = pipeline, Skills = catalog, IsSubAgent = child, InstructionsProvider = () => catalog.Instructions };
        var engine = new AgentEngine(store, provider, codeAct, options);
        await foreach (var evt in engine.RunAsync(conversation.Id, "first", new() { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Content.Contains("replaced") && m.Content.Contains("injected"));
        Assert.Contains(request.Messages, m => m.Content == "request-context");
        Assert.DoesNotContain(sandbox.Prepared, d => d.Name == "file_write");
        Assert.Equal("execute_code", Assert.Single(request.Tools!).Name);
        Assert.Equal("base", conversation.SystemPrompt); Assert.Equal(1, requestCalls);
        enabled = false;
        await foreach (var evt in engine.RunAsync(conversation.Id, "second", new() { DefaultModel = "fixture" })) Assert.IsNotType<AgentError>(evt);
        Assert.Contains(sandbox.Prepared, d => d.Name == "file_write");
        Assert.DoesNotContain(provider.Requests[1].Messages, m => m.Content.Contains("replaced") || m.Content.Contains("injected") || m.Content == "request-context");
        Assert.Equal(1, requestCalls);
    }

    private sealed class Sandbox : ICodeSandbox
    {
        public IReadOnlyList<AgentToolDescriptor> Prepared { get; private set; } = [];
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
        { Prepared = tools.ToArray(); return Task.FromResult<CodeSandboxContext?>(new("fixture", "fixture")); }
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Provider : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { Requests.Add(request); await Task.Yield(); yield return new(ChunkType.TextDelta, "done"); yield return new(ChunkType.Done, FinishReason: "stop"); }
    }
}
