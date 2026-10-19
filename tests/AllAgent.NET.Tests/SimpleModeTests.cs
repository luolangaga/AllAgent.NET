using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Simple mode keeps powershell_script, file_read and file_edit as the only host functions:
/// skills, MCP, subagents, plugins and desktop tools are neither listed to the model nor callable,
/// while the approval stance of the selected permission mode keeps applying.</summary>
public sealed class SimpleModeTests
{
    [Fact]
    public async Task EngineNarrowsTheTurnToPowerShellReadAndEdit()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        catalog.Create("helper", "describes the helper", "helper instructions");
        var conversation = new Conversation(); store.AddConversation(conversation);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "note.txt"), "first line\n");
        // The registry stands in for a host that approves calls; approval stances have their own coverage.
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true));
        registry.Register(workspace.Tools); registry.Register(new SkillTools(catalog));
        var sandbox = new ScriptedSandbox(
            ("file_read", Args(new { path = "note.txt" })),
            ("file_edit", Args(new { path = "note.txt", edits = new[] { new { old_string = "first line", new_string = "edited line" } } })),
            ("file_write", Args(new { path = "blocked.txt", content = "blocked" })),
            ("list_directory", Args(new { path = "." })),
            ("skill_read", Args(new { name = "helper" })));
        var provider = new ScriptedProvider(Call("edit note.txt"), Answer("done"));
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), sandbox);
        // The catalog lists skills per turn, so capturing it here shows what the model was offered.
        var skillsInTurn = new List<IReadOnlyList<InstructionSkill>>();
        var options = workspace.Options with { SimpleMode = true, Skills = catalog, InstructionsProvider = () => { skillsInTurn.Add(catalog.List()); return catalog.Instructions; } };

        var events = await Collect(new AgentEngine(store, provider, codeAct, options).RunAsync(conversation.Id, "edit the note", Config));

        Assert.DoesNotContain(events, e => e is AgentError);
        Assert.Equal(["file_edit", "file_read", "parallel"], sandbox.Prepared.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        var instructions = provider.Requests[0].Messages[0].Content;
        // Simple mode keeps a small prompt: a short core, then the catalog and the runtime context.
        var guidance = instructions[..instructions.IndexOf("## Host tool catalog", StringComparison.Ordinal)];
        Assert.True(guidance.Length < 1400, $"simple-mode guidance is {guidance.Length} characters");
        Assert.Contains("runs in simple mode", guidance);
        Assert.DoesNotContain("## CodeAct execution contract", instructions);
        Assert.DoesNotContain("## Files", instructions);
        Assert.DoesNotContain("## Delegation", instructions);
        // Instruction skills exist in the workspace but simple mode drops them for the turn.
        Assert.Contains(catalog.List(), skill => skill.Name == "helper");
        Assert.NotEmpty(skillsInTurn);
        Assert.All(skillsInTurn, skills => Assert.Empty(skills));
        Assert.DoesNotContain("helper", instructions);
        Assert.True(sandbox.Results[0].Ok, sandbox.Results[0].ToJson());
        Assert.Equal("edited line\n", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "note.txt")));
        Assert.True(sandbox.Results[1].Ok, sandbox.Results[1].ToJson());
        Assert.All(sandbox.Results.Skip(2), result => Assert.False(result.Ok));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "blocked.txt")));
    }

    [Fact]
    public async Task LeavingSimpleModeRestoresTheRegisteredSurfaceOnTheNextTurn()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        catalog.Create("helper", "describes the helper", "helper instructions");
        var conversation = new Conversation(); store.AddConversation(conversation);
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true));
        registry.Register(workspace.Tools); registry.Register(new SkillTools(catalog));
        var sandbox = new ScriptedSandbox(("file_write", Args(new { path = "created.txt", content = "created" })));
        var provider = new ScriptedProvider(Call("first"), Answer("done"), Call("second"), Answer("done"));
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), sandbox);
        var simpleSkills = new List<IReadOnlyList<InstructionSkill>>(); var fullSkills = new List<IReadOnlyList<InstructionSkill>>();
        var simple = workspace.Options with { SimpleMode = true, Skills = catalog, InstructionsProvider = () => { simpleSkills.Add(catalog.List()); return catalog.Instructions; } };
        var full = workspace.Options with { Skills = catalog, InstructionsProvider = () => { fullSkills.Add(catalog.List()); return catalog.Instructions; } };

        await Collect(new AgentEngine(store, provider, codeAct, simple).RunAsync(conversation.Id, "first task", Config));
        Assert.False(sandbox.Results[0].Ok);
        var events = await Collect(new AgentEngine(store, provider, codeAct, full).RunAsync(conversation.Id, "second task", Config));

        Assert.DoesNotContain(events, e => e is AgentError);
        Assert.True(sandbox.Results[1].Ok, sandbox.Results[1].ToJson());
        Assert.Contains(sandbox.Prepared, tool => tool.Name == "file_write");
        Assert.All(simpleSkills, skills => Assert.Empty(skills));
        Assert.All(fullSkills, skills => Assert.Contains(skills, skill => skill.Name == "helper"));
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "created.txt")));
    }

    [Fact]
    public void TheDocumentedSurfaceIsExactlyPowerShellReadAndEdit()
    {
        Assert.Equal(["file_edit", "file_read", "powershell_script"], SimpleModeTools.Names.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, ToolResult.JsonOptions);
    private static StreamChunk[] Call(string id) => [new(ChunkType.ToolCallDelta, ToolCall: new(id, "execute_code", "{\"code\":")), new(ChunkType.ToolCallDelta, ToolCall: new(id, "", "\"print(1)\"}")), new(ChunkType.ToolCallComplete, ToolCall: new(id, "execute_code", "{\"code\":\"print(1)\"}"))];
    private static StreamChunk[] Answer(string text) => [new(ChunkType.TextDelta, TextDelta: text), new(ChunkType.Done, FinishReason: "stop")];
    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> stream) { var list = new List<AgentEvent>(); await foreach (var evt in stream) list.Add(evt); return list; }
    private static ApiConfig Config => new() { ProviderId = "openai", DefaultModel = "test-model", BaseUrl = "http://127.0.0.1" };

    private sealed class ScriptedSandbox(params (string Name, JsonElement Args)[] calls) : ICodeSandbox
    {
        public IReadOnlyList<AgentToolDescriptor> Prepared { get; private set; } = [];
        public List<ToolResult> Results { get; } = [];
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
        { Prepared = tools.ToArray(); return Task.FromResult<CodeSandboxContext?>(new("fixture", "fixture")); }
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            foreach (var (name, arguments) in calls) Results.Add(await callback(name, arguments, ct));
            return ToolResult.Success(new { stdout = "", stderr = "", exitCode = 0 });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ScriptedProvider(params StreamChunk[][] responses) : IAiProvider
    {
        public string ProviderId => "openai";
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (var chunk in responses[Requests.Count - 1]) { ct.ThrowIfCancellationRequested(); yield return chunk; }
            if (!responses[Requests.Count - 1].Any(chunk => chunk.Type is ChunkType.Done or ChunkType.Error)) yield return new(ChunkType.Done, FinishReason: "tool_calls");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }
}
