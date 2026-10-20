using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PlanModeTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task ApprovedPlanIsPersistedAndEnablesFullAccess()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var approval = new ToolsApproval(new TerminalUi(json: true, verbose: false))
        {
            PlanMode = true,
            Permission = PermissionMode.Ask,
        };
        PlanProposal? seen = null;
        var tools = new PlanTools(store, (plan, _) =>
        {
            seen = plan;
            approval.PlanMode = false;
            approval.Permission = PermissionMode.Full;
            return Task.FromResult(new PlanDecision(true, Mode: "full"));
        }, () => approval.PlanMode);

        using var scope = AgentExecutionScope.Enter(false, conversation.Id, workspace.Root);
        var result = await tools.ProposeAsync("Add a feature", "# Plan\n\n1. Read the code\n2. Make the change\n3. Verify it");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Add a feature", seen?.Title);
        var stored = store.GetPlan(conversation.Id);
        Assert.NotNull(stored);
        Assert.Equal("approved", stored!.Status);
        Assert.Equal("Add a feature", stored.Title);
        using var arguments = JsonDocument.Parse("{\"path\":\"note.txt\"}");
        Assert.True(await approval.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None));
    }

    [Fact]
    public async Task RejectedPlanStaysReadOnlyAndKeepsFeedback()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var tools = new PlanTools(store, (_, _) => Task.FromResult(new PlanDecision(false, "Add a rollback step.")), () => true);

        using var scope = AgentExecutionScope.Enter(false, conversation.Id, workspace.Root);
        var result = await tools.ProposeAsync("Risky change", "# Plan\n\nDo the risky change.");

        Assert.True(result.Ok, result.Error);
        var stored = store.GetPlan(conversation.Id);
        Assert.Equal("rejected", stored!.Status);
        Assert.Equal("Add a rollback step.", stored.Feedback);
        Assert.Contains("Plan rejected", JsonSerializer.Serialize(result.Data, ToolResult.JsonOptions));
    }

    [Fact]
    public async Task PlanToolIsOnlyAvailableInPlanMode()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var tools = new PlanTools(store, (_, _) => Task.FromResult(new PlanDecision(true)), () => false);

        using var scope = AgentExecutionScope.Enter(false, conversation.Id, workspace.Root);
        var result = await tools.ProposeAsync("Plan", "# Plan");

        Assert.False(result.Ok);
        Assert.Contains("only available in plan mode", result.Error);
        Assert.Null(store.GetPlan(conversation.Id));
    }

    [Fact]
    public async Task DelegatedAgentsCannotProposePlans()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var tools = new PlanTools(store, (_, _) => Task.FromResult(new PlanDecision(true)), () => true);

        using var scope = AgentExecutionScope.Enter(true, conversation.Id, workspace.Root);
        var result = await tools.ProposeAsync("Plan", "# Plan");

        Assert.False(result.Ok);
        Assert.Contains("Delegated agents", result.Error);
    }

    [Fact]
    public async Task OversizedPlanIsRejectedBeforeApproval()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var called = false;
        var tools = new PlanTools(store, (_, _) => { called = true; return Task.FromResult(new PlanDecision(true)); }, () => true);

        using var scope = AgentExecutionScope.Enter(false, conversation.Id, workspace.Root);
        var result = await tools.ProposeAsync("Too large", new string('x', 64_001));

        Assert.False(result.Ok);
        Assert.False(called);
        Assert.Null(store.GetPlan(conversation.Id));
    }

    [Fact]
    public async Task PlanSurfaceAppearsOnlyWhilePlanModeIsActive()
    {
        using var workspace = new TestWorkspace();
        var planMode = true;
        var registry = new ToolRegistry();
        registry.Register(workspace.Tools);
        registry.Register(new PlanTools(null, (_, _) => Task.FromResult(new PlanDecision(true)), () => planMode));
        await using var provider = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), new EmptySandbox())
        {
            ReadOnlyProvider = () => planMode,
            SimpleModeProvider = () => false,
        };

        var planning = provider.Instructions;
        Assert.Contains("plan_propose", planning);
        Assert.Contains("## Plan approval", planning);
        Assert.DoesNotContain("call_tool(\"file_edit\"", planning);

        planMode = false;
        var executing = provider.Instructions;
        Assert.DoesNotContain("plan_propose", executing);
        Assert.DoesNotContain("## Plan approval", executing);
        Assert.Contains("file_edit", executing);
    }

    [Fact]
    public async Task BrowserBrokerReplaysAndResolvesAPlan()
    {
        var dispatcher = new BridgeDispatcher();
        var pushed = new List<string>();
        var pusher = new BridgeEventPusher();
        pusher.SetPusher(json => pushed.Add(json));
        var approved = false;
        var approvals = new WebApprovals(dispatcher, pusher)
        {
            HasClients = () => true,
            OnPlanApproved = _ => { approved = true; return Task.CompletedTask; },
        };

        var pending = approvals.ProposePlanAsync(new PlanProposal("Browser plan", "# Plan\n\nDo it."), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && pushed.Count == 0; attempt++) await Task.Delay(10);
        Assert.Single(pushed);
        var proposal = JsonDocument.Parse(pushed[0]).RootElement;
        Assert.Equal("agent.planProposed", proposal.GetProperty("type").GetString());
        var planId = proposal.GetProperty("id").GetString();
        Assert.Equal("Browser plan", proposal.GetProperty("title").GetString());
        Assert.Equal("# Plan\n\nDo it.", proposal.GetProperty("markdown").GetString());

        approvals.ReplayPending();
        Assert.Equal(2, pushed.Count);

        await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "1", method = "plan.respond", @params = new { planId, approved = true } }, JsonOptions.Default));
        var decision = await pending;
        Assert.True(decision.Approved);
        Assert.True(approved);
        Assert.Contains(pushed, json => json.Contains("plans.updated") && json.Contains("\"approved\""));
        Assert.Contains(pushed, json => json.Contains("interactions.resolved"));
    }

    [Fact]
    public async Task BrowserBrokerWithoutAClientRejectsInsteadOfHanging()
    {
        var dispatcher = new BridgeDispatcher();
        var pusher = new BridgeEventPusher();
        var approvals = new WebApprovals(dispatcher, pusher) { HasClients = () => false };
        var decision = await approvals.ProposePlanAsync(new PlanProposal("Offline", "# Plan"), CancellationToken.None);
        Assert.False(decision.Approved);
        Assert.Contains("No browser client", decision.Feedback);
    }

    [Fact]
    public void PlanStoreValidatesAndVersions()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        var before = store.PlansRevision;
        var plan = new PlanDocument("p1", "Plan", "# Plan", "proposed", null, DateTime.UtcNow, null);
        store.SetPlan(conversation.Id, plan);
        Assert.Equal(before + 1, store.PlansRevision);
        Assert.Equal(plan, store.GetPlan(conversation.Id));
        Assert.Throws<ArgumentException>(() => store.SetPlan(conversation.Id,
            plan with { Markdown = new string('x', 64_001) }));
    }

    [Theory]
    [InlineData("framework")]
    [InlineData("legacy")]
    public async Task ApprovedPlanRefreshesTheContractAndAllowsTheSameTurnToWrite(string kernel)
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "note.txt"), "first line\n");

        var planMode = true;
        var permission = PermissionMode.Ask;
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(permission == PermissionMode.Full));
        registry.Register(workspace.Tools);
        var sandbox = new ScriptedSandbox(
            ("plan_propose", Args(new { title = "Edit the note", markdown = "# Plan\n\nReplace the first line in note.txt." })),
            ("file_edit", Args(new { path = "note.txt", edits = new[] { new { old_string = "first line", new_string = "edited line" } } })));
        var provider = new ScriptedProvider(Call("1"), Call("2"), Answer("done"));
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), sandbox)
        {
            ReadOnlyProvider = () => planMode,
        };
        var options = workspace.Options with
        {
            AgentKernel = kernel,
            Permission = PermissionMode.Ask,
            PermissionProvider = () => permission,
            ReadOnlyProvider = () => planMode,
            PlanHandler = (_, _) =>
            {
                planMode = false;
                permission = PermissionMode.Full;
                return Task.FromResult(new PlanDecision(true, Mode: "full"));
            },
            InstructionsProvider = () => "",
        };

        var events = await Collect(new AgentEngine(store, provider, codeAct, options).RunAsync(conversation.Id, "plan then edit", Config));

        Assert.DoesNotContain(events, e => e is AgentError);
        Assert.True(sandbox.Results[0].Ok, sandbox.Results[0].ToJson());
        Assert.True(sandbox.Results[1].Ok, sandbox.Results[1].ToJson());
        Assert.Equal("edited line\n", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "note.txt")));
        Assert.Contains("plan_propose", provider.Requests[0].Messages[0].Content);
        Assert.Contains("file_edit", provider.Requests[1].Messages[0].Content);
        Assert.DoesNotContain("plan_propose", provider.Requests[1].Messages[0].Content);
        Assert.Equal("approved", store.GetPlan(conversation.Id)!.Status);
    }

    [Fact]
    public async Task BackgroundPlanPromptWaitsOnTheRunAndReleasesOnFinish()
    {
        using var cancellation = new CancellationTokenSource();
        var run = new SessionRun(Guid.NewGuid(), "Plan session", "stream", new BridgeStreamJournal([], ""), cancellation);
        var completion = new TaskCompletionSource<bool>();
        var preview = new PlanPreview(new PlanProposal("Plan", "# Plan"));
        run.EnqueuePlan(new PendingPlan(preview, completion, CancellationToken.None));
        Assert.Equal(1, run.PendingPrompts);
        Assert.True(run.TryDequeuePlan(out var pending));
        Assert.Same(preview, pending!.Preview);

        run.EnqueuePlan(new PendingPlan(preview, completion, CancellationToken.None));
        run.ReleasePrompts();
        Assert.True(completion.Task.IsCompleted);
        Assert.False(await completion.Task);
        Assert.Equal(0, run.PendingPrompts);
    }

    private sealed class EmptySandbox : ICodeSandbox
    {
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) =>
            Task.FromResult<CodeSandboxContext?>(null);
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) =>
            Task.FromResult(ToolResult.Failure("not used"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedSandbox(params (string Name, JsonElement Args)[] calls) : ICodeSandbox
    {
        private int _index;
        public List<ToolResult> Results { get; } = [];
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) =>
            Task.FromResult<CodeSandboxContext?>(new("fixture", "fixture"));
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            while (_index < calls.Length)
            {
                var (name, arguments) = calls[_index++];
                Results.Add(await callback(name, arguments, ct));
            }
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
            if (!responses[Requests.Count - 1].Any(chunk => chunk.Type is ChunkType.Done or ChunkType.Error))
                yield return new(ChunkType.Done, FinishReason: "tool_calls");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private static StreamChunk[] Call(string id) =>
    [
        new(ChunkType.ToolCallDelta, ToolCall: new(id, "execute_code", "{\"code\":")),
        new(ChunkType.ToolCallDelta, ToolCall: new(id, "", "\"print(1)\"}")),
        new(ChunkType.ToolCallComplete, ToolCall: new(id, "execute_code", "{\"code\":\"print(1)\"}"))
    ];
    private static StreamChunk[] Answer(string text) => [new(ChunkType.TextDelta, TextDelta: text), new(ChunkType.Done, FinishReason: "stop")];
    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> stream)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in stream) list.Add(evt);
        return list;
    }
    private static ApiConfig Config => new() { ProviderId = "openai", DefaultModel = "test-model", BaseUrl = "http://127.0.0.1" };
}
