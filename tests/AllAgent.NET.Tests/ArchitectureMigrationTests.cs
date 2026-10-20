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

public sealed class ArchitectureMigrationTests
{
    [Fact]
    public async Task ChildInstructionsAndGatewayCannotExposeDelegationEvenWithRegisteredTools()
    {
        var registry = new ToolRegistry(); registry.Register(new Probe("subagent")); registry.Register(new Probe("ask_user"));
        await using var code = new CodeActProvider(registry, new("unused"), new EmptySandbox()) { IsSubAgent = true };
        Assert.DoesNotContain("## Delegation\n", code.Instructions);
        Assert.Contains("## Delegated execution", code.Instructions);
        Assert.DoesNotContain("Available host tools: parallel, subagent", code.ModelTools.Single().Description);
        Assert.False((await code.CallToolAsync("subagent", Empty(), default)).Ok);
        Assert.False((await code.CallToolAsync("ask_user", Empty(), default)).Ok);
    }

    [Fact]
    public void PromptManifestContainsHashesAndSourcesWithoutInstructionBodies()
    {
        var block = new InstructionBlock("project", "AGENTS.md", "private-project-marker");
        var prompt = PromptComposer.Compose([block, block]);
        Assert.Single(prompt.Manifest.Blocks);
        Assert.Equal(block.Text, prompt.Text);
        Assert.DoesNotContain(block.Text, JsonSerializer.Serialize(prompt.Manifest));
        Assert.Throws<InvalidOperationException>(() => PromptComposer.Compose([block, block with { Text = "different" }]));
    }

    [Fact]
    public void ProjectAndSkillSourcesAreCapturedWithoutDuplicatingTheirText()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        File.WriteAllText(Path.Combine(workspace.Root, "AGENTS.md"), "SOURCE-PROOF");
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "skills"), workspace.Root);
        var message = PromptComposer.GuidanceMessage("session", () => ProjectInstructions.Load(workspace.Root) + "\n" + catalog.Instructions);
        Assert.Contains(message.InstructionSources, block => block.Source == Path.Combine(workspace.Root, "AGENTS.md"));
        Assert.Contains(message.InstructionSources, block => block.Id == "skills:catalog");
        Assert.Equal(1, message.Content.Split("SOURCE-PROOF").Length - 1);
    }

    [Fact]
    public void TaskContractRestrictsTypedFileWritesWithoutClaimingToSandboxProcesses()
    {
        using var workspace = new TestWorkspace();
        var contract = new SubAgentTaskContract("fix") { EditablePaths = ["src/**"] };
        var write = new AgentToolDescriptor("file_write", "write", Empty(), ToolRisk.Write);
        Assert.Null(contract.ValidateInvocation(write, JsonSerializer.SerializeToElement(new { path = "src/ok.cs" }), workspace.Root));
        Assert.NotNull(contract.ValidateInvocation(write, JsonSerializer.SerializeToElement(new { path = "src/../outside.cs" }), workspace.Root));
        Assert.NotNull((contract with { EditablePaths = [] }).ValidateInvocation(write, JsonSerializer.SerializeToElement(new { path = "src/no.cs" }), workspace.Root));
        Assert.Null(contract.ValidateInvocation(write with { Name = "run_shell" }, Empty(), workspace.Root));
        Assert.True(SubAgentTaskContract.Overlaps("src/../src/**", "./src/app.cs", workspace.Root));
        Assert.False(SubAgentTaskContract.Overlaps("src/app.cs", "src2/app.cs", workspace.Root));
    }

    [Fact]
    public async Task ChangedPermissionsDuringApprovalNeverExecuteTheOldGrant()
    {
        var version = "full"; var calls = 0;
        var registry = new ToolRegistry((_, _) => { version = "restricted"; return ValueTask.FromResult(true); }, authorizationVersion: () => version);
        registry.Register(new WriteProbe(() => calls++));
        var result = await registry.CallAsync("write_probe", Empty(), default);
        Assert.False(result.Ok); Assert.Equal(0, calls); Assert.Contains("permissions changed", result.Error);
    }

    [Fact]
    public void RoleEditsCanTightenButCannotExpandTheDispatchGrant()
    {
        using var workspace = new TestWorkspace();
        var grant = new AgentRoleDefinition("bounded", "test", "test", Tools: ["file_read"]);
        var live = grant with { Tools = null };
        var policy = new SubAgentRolePolicy(() => live.ReadOnly, workspace.Root, role: () => live, grant: grant);
        Assert.NotNull(policy.Validate(new("file_write", "write", Empty(), ToolRisk.Write), JsonSerializer.SerializeToElement(new { path = "result" })));
        live = live with { ReadOnly = true };
        Assert.NotNull(policy.Validate(new("run_shell", "shell", Empty(), ToolRisk.Execute), Empty()));
    }

    [Fact]
    public async Task RequestExtensionCannotRemoveHostContractOrExpandTheToolProtocol()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var extensions = new AllAgent.NET.Core.Extensions.AgentExtensionPipeline(() => [], () => [new("replace", (context, _) =>
        { context.Messages[0] = context.Messages[0] with { Content = "PLUGIN-CONTEXT" }; return ValueTask.CompletedTask; })]);
        var provider = new Answers(); await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        await foreach (var _ in new AgentEngine(store, provider, code, workspace.Options with { Extensions = extensions, AgentKernel = "framework" })
            .RunAsync(conversation.Id, "task", new() { DefaultModel = "fixture" })) { }
        Assert.StartsWith("You are AllAgent", provider.LastRequest!.Messages[0].Content);
        Assert.Single(provider.LastRequest.Tools!); Assert.Equal("execute_code", provider.LastRequest.Tools![0].Name);
        Assert.DoesNotContain("PLUGIN-CONTEXT", provider.LastRequest.Messages[0].Content);
    }

    [Fact]
    public async Task GuestCanAggregateDataBeyondTheDisplayBudget()
    {
        var registry = new ToolRegistry(); registry.Register(new Probe("large", new string('x', 100_000)));
        await using var code = new CodeActProvider(registry, new("unused"), new EmptySandbox());
        var preview = await registry.CallAsync("large", Empty(), default);
        var guest = await code.CallToolAsync("large", Empty(), default);
        Assert.True(preview.Truncated); Assert.False(guest.Truncated);
        Assert.Equal(100_000, JsonSerializer.SerializeToElement(guest.Data).GetProperty("value").GetString()!.Length);
    }

    [Fact]
    public void RuntimeInitializerCanRefreshSeveralSetsOfTheSameTypeWithoutDroppingSiblings()
    {
        var registry = new ToolRegistry(); registry.Register(new Probe("old_a")); registry.Register(new Probe("old_b"));
        using (registry.RefreshRegistration())
        {
            registry.Register(new Probe("new_a")); registry.Register(new Probe("new_b"));
            Assert.Throws<InvalidOperationException>(() => registry.Register(new Probe("new_a")));
        }
        Assert.Equal(new[] { "new_a", "new_b" }, registry.Definitions.Select(tool => tool.Name));
    }

    [Fact]
    public async Task WaitAgentsEnforcesOwnershipAndDoesNotRepeatGrowingOutput()
    {
        await using var jobs = new BackgroundTasks(); var parent = Guid.NewGuid();
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { yield return new TokenDelta("GROWING-OUTPUT"); await Task.Delay(Timeout.Infinite, ct); }
        var job = jobs.Start("child", Guid.NewGuid(), "agent", Run, parent);
        var tools = new AgentDelegationTools(jobs, (_, _) => throw new Exception("No launch"));
        using (AgentExecutionScope.Enter(false, parent))
        {
            var result = await tools.InvokeAsync("wait_agents", JsonSerializer.SerializeToElement(new { ids = new[] { job }, timeout_seconds = 0 }), default);
            Assert.True(result.Ok); Assert.DoesNotContain("GROWING-OUTPUT", result.ToJson()); Assert.Contains("\"pending\":1", result.ToJson());
        }
        using (AgentExecutionScope.Enter(false, Guid.NewGuid()))
            Assert.False((await tools.InvokeAsync("wait_agents", JsonSerializer.SerializeToElement(new { ids = new[] { job } }), default)).Ok);
        jobs.Cancel(job); await jobs.WaitAsync(job);
    }

    [Fact]
    public async Task OverlappingParallelContractsAreRejectedBeforeAnyLaunch()
    {
        await using var jobs = new BackgroundTasks(); var launches = 0;
        var tools = new AgentDelegationTools(jobs, (_, _) => { launches++; throw new Exception("No launch"); });
        var result = await tools.InvokeAsync("subagent", JsonSerializer.SerializeToElement(new { tasks = new[] {
            new { agent = "modify", task = "one", contract = new SubAgentTaskContract("one") { EditablePaths = ["src/**"] } },
            new { agent = "modify", task = "two", contract = new SubAgentTaskContract("two") { EditablePaths = ["src/app.cs"] } }
        } }, ToolResult.JsonOptions), default);
        Assert.False(result.Ok); Assert.Equal(0, launches);
    }

    [Fact]
    public async Task FramesRejectVersionExecutionDepthAndByteViolations()
    {
        using var valid = await SandboxProtocol.ReadAsync(new StringReader(SandboxProtocol.Encode(new { kind = "result" }, "one") + "\n"));
        SandboxProtocol.Validate(valid.RootElement, "one");
        Assert.Throws<InvalidDataException>(() => SandboxProtocol.Validate(valid.RootElement, "two"));
        using var old = JsonDocument.Parse("{\"kind\":\"result\"}");
        Assert.Throws<InvalidDataException>(() => SandboxProtocol.Validate(old.RootElement, null));
        await Assert.ThrowsAnyAsync<JsonException>(() => SandboxProtocol.ReadAsync(new StringReader(new string('[', 65) + "0" + new string(']', 65) + "\n")));
        Assert.Throws<InvalidDataException>(() => SandboxProtocol.Encode(new { text = new string('中', SandboxProtocol.MaxFrameBytes / 3) }, "one"));
        await Assert.ThrowsAsync<InvalidDataException>(() => SandboxProtocol.ReadAsync(new StringReader(new string('x', SandboxProtocol.MaxFrameBytes + 1))));
    }

    [Fact]
    public async Task PipeFramesFinishAtNewlineWithoutWaitingForBufferFillOrEof()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        var first = SandboxProtocol.Encode(new { kind = "ready" }, null);
        var second = SandboxProtocol.Encode(new { kind = "result" }, "next");
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(first + "\n" + second + "\n"));
        await using var input = pipe.Reader.AsStream();
        using var ready = await SandboxProtocol.ReadAsync(input).WaitAsync(TimeSpan.FromSeconds(2));
        SandboxProtocol.Validate(ready.RootElement, null);
        using var result = await SandboxProtocol.ReadAsync(input).WaitAsync(TimeSpan.FromSeconds(2));
        SandboxProtocol.Validate(result.RootElement, "next");
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task ViewsShareDataDirectoryLeaseAndCannotReconfigureAnActiveConversation()
    {
        using var workspace = new TestWorkspace();
        var first = ConversationRuntimePool.Acquire(Path.Combine(workspace.Root, "data"));
        var second = ConversationRuntimePool.Acquire(Path.Combine(workspace.Root, "data"));
        Assert.Same(first.Factory, second.Factory);
        var id = Guid.NewGuid(); var runtime = await first.Factory.GetAsync(id, workspace.Options);
        Assert.Same(runtime.CodeAct.Surface, second.Factory.SurfaceFor(workspace.Root, id));
        using (runtime.CodeAct.EnterRun())
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.Factory.GetAsync(id, workspace.Options));
        await first.DisposeAsync();
        Assert.Same(runtime, await second.Factory.GetAsync(id, workspace.Options));
        await second.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.Factory.GetAsync(id, workspace.Options));
    }

    [Fact]
    public async Task ReadOnlySurfaceDoesNotSuggestWritingOrMisidentifySimpleMode()
    {
        var registry = new ToolRegistry(); registry.Register(new Probe("file_read")); registry.Register(new WriteProbe(() => { }));
        await using var code = new CodeActProvider(registry, new("unused"), new EmptySandbox()) { ReadOnlyProvider = () => true };
        Assert.DoesNotContain("runs in simple mode", code.Instructions);
        Assert.DoesNotContain("call_tool(\"file_edit\"", code.Instructions);
        Assert.DoesNotContain("write_probe", code.ModelTools.Single().Description);
        Assert.False((await code.CallToolAsync("write_probe", Empty(), default)).Ok);
    }

    [Fact]
    public async Task SimpleModeAlsoProtectsDirectEmbeddingCallsOutsideTheEngine()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { SimpleMode = true, Permission = PermissionMode.Full });
        var args = JsonSerializer.SerializeToElement(new { path = "blocked.txt", content = "blocked" });
        Assert.False((await runtime.CodeAct.CallToolAsync("file_write", args, default)).Ok);
        Assert.False((await runtime.Tools.CallAsync("file_write", args, default)).Ok);
        Assert.DoesNotContain("file_write", runtime.CodeAct.ModelTools.Single().Description);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "blocked.txt")));
    }

    [Theory]
    [InlineData(PermissionMode.Restricted, PermissionMode.Full, PermissionMode.Restricted)]
    [InlineData(PermissionMode.Full, PermissionMode.Ask, PermissionMode.Ask)]
    [InlineData(PermissionMode.Ask, PermissionMode.Full, PermissionMode.Ask)]
    public void ChildGrantCanOnlyTighten(PermissionMode grant, PermissionMode current, PermissionMode expected)
        => Assert.Equal(expected, DelegatedPermissions.Restrict(grant, current));

    [Fact]
    public async Task RoleConcurrencyLimitIsSharedByStoreInstancesOfTheSameDirectory()
    {
        using var workspace = new TestWorkspace();
        using var first = new DataStore(Path.Combine(workspace.Root, "data"));
        using var second = new DataStore(Path.Combine(workspace.Root, "data"));
        var lease = await SubAgentConcurrency.AcquireAsync(first, "bounded", 1, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = SubAgentConcurrency.AcquireAsync(second, "bounded", 1, timeout.Token);
        await Task.Delay(100); Assert.False(waiting.IsCompleted);
        lease.Dispose(); using var next = await waiting;
    }

    [Fact]
    public async Task RuntimeFactoryReusesOneConversationAndIsolatesOtherConversations()
    {
        using var workspace = new TestWorkspace(); await using var factory = new ConversationRuntimeFactory();
        var id = Guid.NewGuid(); var first = await factory.GetAsync(id, workspace.Options);
        first.CodeAct.Surface.Sync([new("test-skill", ["probe"]) ]); first.CodeAct.Surface.Activate("test-skill");
        var other = await factory.GetAsync(Guid.NewGuid(), workspace.Options);
        Assert.NotSame(first, other); Assert.False(other.CodeAct.Surface.IsActive("test-skill"));
        Assert.Same(first, await factory.GetAsync(id, workspace.Options));
        var currentContract = new SubAgentTaskContract("scope") { EditablePaths = ["allowed.txt"] };
        var refreshed = await factory.GetAsync(id, workspace.Options with { TaskContractProvider = () => currentContract });
        Assert.Same(first, refreshed);
        Assert.Same(currentContract, refreshed.Options.TaskContractProvider!());
        Assert.NotSame(first, await factory.GetAsync(id, workspace.Options with { Permission = PermissionMode.Full }));
    }

    [Fact]
    public async Task FrameworkWaitsForRequiredBackgroundEvidenceBeforeCompleting()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent);
        var child = new Conversation { ParentConversationId = parent.Id, Delegation = new("child") }; store.AddConversation(child);
        await using var jobs = new BackgroundTasks(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        { await release.Task.WaitAsync(ct); yield return new TokenDelta("CHILD-EVIDENCE"); yield return new AgentDone("stop"); }
        var job = jobs.Start("child", child.Id, "agent", Run, parent.Id); jobs.NotifyParentOnCompletion(job);
        var provider = new Answers(); await using var code = new CodeActProvider(new ToolRegistry(), new("unused"), new EmptySandbox());
        var events = new List<AgentEvent>();
        async Task Consume() { await foreach (var evt in new AgentEngine(store, provider, code, workspace.Options with { AgentKernel = "framework", BackgroundTasks = jobs }).RunAsync(parent.Id, "task", new() { DefaultModel = "fixture" })) events.Add(evt); }
        var run = Consume(); await Task.Delay(100); Assert.False(run.IsCompleted); release.SetResult(); await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, provider.Calls); Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Single(store.GetMessages(parent.Id), message => message.Content == "task");
        Assert.Contains(store.GetMessages(parent.Id), message => message.IsInternal && message.Content.Contains("CHILD-EVIDENCE"));
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });
    private sealed class EmptySandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => Task.FromResult(ToolResult.Success(null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Probe(string name, string value = "value") : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new(name, "probe", JsonSerializer.SerializeToElement(new { type = "object" }), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct) => ValueTask.FromResult(ToolResult.Success(new { value }));
    }
    private sealed class WriteProbe(Action invoke) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new("write_probe", "write", Empty(), ToolRisk.Write)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct) { invoke(); return ValueTask.FromResult(ToolResult.Success(null)); }
    }
    private sealed class Answers : IAiProvider
    {
        public int Calls; public string ProviderId => "fixture"; public ChatRequest? LastRequest;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.Yield(); Calls++; LastRequest = request; yield return new(ChunkType.TextDelta, TextDelta: "response"); yield return new(ChunkType.Done, FinishReason: "stop"); }
    }
}

[Collection("Hyperlight acceptance")]
public sealed class SandboxBackendAcceptanceTests
{
    [Theory]
    [InlineData(SandboxMode.Hyperlight, "hyperlight")]
    [InlineData(SandboxMode.Monty, "monty")]
    public async Task ExplicitBackendExecutesHostCallsAndClearsGuestState(SandboxMode mode, string expected)
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { SandboxMode = mode,
            SharedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET"), AllowSandboxInstallation = false });
        var first = await Execute(runtime, "r = call_tool('list_directory', path='.')\nx = 42\nprint(r['ok'])");
        Assert.True(first.Ok, first.ToJson()); Assert.Contains(expected, runtime.SandboxName, StringComparison.OrdinalIgnoreCase);
        Assert.False((await Execute(runtime, "print(x)")).Ok);
        Assert.True((await Execute(runtime, "print(42)")).Ok);
    }

    [Fact]
    public async Task AutoFallsBackBeforeExecutionWhenHyperlightWorkerIsMissing()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { SandboxMode = SandboxMode.Auto,
            SandboxWorkerPath = Path.Combine(workspace.Root, "absent.exe"), SharedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET"), AllowSandboxInstallation = false });
        Assert.True((await Execute(runtime, "print(42)")).Ok); Assert.Contains("Monty", runtime.SandboxName); Assert.NotNull(runtime.SandboxFallbackReason);
    }

    private static Task<ToolResult> Execute(AgentRuntime runtime, string source) => runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = source }), runtime.CodeAct.CallToolAsync, default);
}
