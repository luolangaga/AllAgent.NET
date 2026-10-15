using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Services;

public abstract record AgentEvent;
public record TokenDelta(string Text) : AgentEvent;
public record ThinkingDelta(string Text) : AgentEvent;
public record ToolCallStarted(string Id, string ToolName, string ArgsJson) : AgentEvent
{
    public string? ParentId { get; init; }
}
public record ToolCallRejected(string Id, string ToolName, string Reason) : AgentEvent;
public record ToolCallOutput(string Id, string Line, bool IsError) : AgentEvent;
public record ToolCallCompleted(string Id, string ToolName, string ResultJson) : AgentEvent;
public sealed record HostToolTrace(string Id, string ToolName, string ArgsJson, string ResultJson);
public record AgentIteration(int Current, int Maximum, int ToolCalls) : AgentEvent;
public record AgentDone(string FinishReason) : AgentEvent;
public record AgentError(string Message) : AgentEvent;
public record AgentNotice(string Message) : AgentEvent
{
    public bool ResetResponse { get; init; }
}
public record AgentInputAdmitted(Message Message) : AgentEvent;
public record AgentContextUsage(int UsedTokens, int CapacityTokens, bool Estimated) : AgentEvent;

public sealed class AgentService : IAsyncDisposable
{
    public BackgroundTasks Jobs { get; }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _streams.Keys) CancelStream(id);
        await Task.WhenAll(_completions.Values.Select(completion => completion.Task));
        await Jobs.DisposeAsync();
    }
    private readonly DataStore _store;
    private readonly AiProviderFactory _factory;
    private readonly AgentOptions _options;
    private readonly McpToolService? _mcp;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _completions = new();
    private readonly ConcurrentDictionary<Guid, byte> _conversations = new();

    public AgentService(DataStore store, AiProviderFactory providerFactory, McpToolService toolService)
        : this(store, providerFactory, new AgentOptions(), toolService) { }
    public AgentService(DataStore store, AiProviderFactory providerFactory, AgentOptions options)
        : this(store, providerFactory, options, null) { }
    public AgentService(DataStore store, AiProviderFactory providerFactory, AgentOptions options, McpToolService? mcp)
    { _store = store; _factory = providerFactory; _options = options; _mcp = mcp; Jobs = options.BackgroundTasks ?? new(); }

    public string StartStream(Guid conversationId, string userMessage, ApiConfig config,
        Func<string, AgentEvent, Task> onEvent, CancellationToken externalCt = default)
    {
        if (!_conversations.TryAdd(conversationId, 0)) throw new InvalidOperationException("This conversation already has an active run.");
        var id = Guid.NewGuid().ToString("N");
        var source = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _streams[id] = source;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _completions[id] = completion;
        _ = Task.Run(async () =>
        {
            try
            {
                // Host-owned jobs remain inspectable after the parent turn completes.
                var children = Jobs;
                var runtimeOptions = _options with { BackgroundTasks = Jobs };
                await using var runtime = await AgentRuntime.CreateAsync(runtimeOptions, source.Token);
                if (_mcp != null) runtime.Tools.Register(new McpToolSet(_mcp));
                if (!_options.IsSubAgent)
                    runtime.Tools.Register(new AgentDelegationTools(children, (request, token) =>
                    {
                        var role = new AgentRoleCatalog(_store).Get(request.Agent);
                        if (!role.ReadOnly) _options.Checkpoints?.MarkExternalAction();
                        var selected = SubAgentModelSelection.Resolve(config, _store.ApiConfigs,
                            role.Model ?? _store.GetSetting("subagents:model"), role.Provider ?? _store.GetSetting("subagents:provider"));
                        var child = new Conversation { ParentConversationId = conversationId, WorkspaceRoot = _options.WorkspaceRoot, AgentRole = role.Name, Title = request.Description, ModelId = selected.DefaultModel };
                        _store.AddConversation(child);
                        return Task.FromResult(children.Start(request.Description, child.Id, "agent", (job, ct) => RunChildAsync(child.Id, request.Prompt, selected, token, ct), conversationId, ownerToken: token));
                    }));
                var probe = await runtime.ProbeSandboxAsync(source.Token);
                if (!probe.Ok) throw new InvalidOperationException(probe.Error);
                var engine = new AgentEngine(_store, _factory.GetProvider(config), runtime.CodeAct, runtimeOptions);
                await foreach (var evt in engine.RunAsync(conversationId, userMessage, config, source.Token)) await onEvent(id, evt);
            }
            catch (OperationCanceledException) { await onEvent(id, new AgentDone("cancelled")); }
            catch (Exception ex) { await onEvent(id, new AgentError(ex.Message)); await onEvent(id, new AgentDone("error")); }
            finally { _streams.TryRemove(id, out _); _conversations.TryRemove(conversationId, out _); source.Dispose(); completion.TrySetResult(); _completions.TryRemove(id, out _); }
        });
        return id;
    }

    private async IAsyncEnumerable<AgentEvent> RunChildAsync(Guid conversationId, string task, ApiConfig config,
        CancellationToken parentToken, [EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, ct);
        var role = new AgentRoleCatalog(_store).Get(_store.GetConversation(conversationId)?.AgentRole ?? "general");
        var rolePolicy = new SubAgentRolePolicy(() => role.ReadOnly, _options.WorkspaceRoot, _options.ExternalDirectoryPermissions, () => role);
        var options = _options with
        {
            BackgroundTasks = Jobs,
            IsSubAgent = true,
            HostToolsProvider = () => role.Tools,
            FullAccess = _options.FullAccess && !role.ReadOnly,
            InvocationPolicy = (tool, arguments) => rolePolicy.Validate(tool, arguments) ?? _options.InvocationPolicy?.Invoke(tool, arguments),
            InvocationAuthorization = async (tool, arguments, token) => ( _options.InvocationAuthorization == null ? null : await _options.InvocationAuthorization(tool, arguments, token)) ?? await rolePolicy.AuthorizeAsync(tool, arguments, token),
            Checkpoints = null,
            InstallationApprover = null,
            QuestionHandler = null,
            Approver = (request, _) => ValueTask.FromResult(request.Preapproved),
            InstructionsProvider = () => (_options.InstructionsProvider?.Invoke() ?? ProjectInstructions.Load(_options.WorkspaceRoot)) +
                "\nYou are a delegated agent. Stay within your assigned task, report evidence and limitations, and do not spawn agents or ask the user. Return missing information and permission blockers to the parent in your final response. Do not bypass host permissions.\n" + role.Instructions
        };
        await using var runtime = await AgentRuntime.CreateAsync(options, linked.Token);
        if (_mcp != null) runtime.Tools.Register(new McpToolSet(_mcp));
        var engine = new AgentEngine(_store, _factory.GetProvider(config), runtime.CodeAct, options);
        await foreach (var evt in engine.RunAsync(conversationId, task, config, linked.Token)) yield return evt;
    }

    public void CancelStream(string streamId)
    {
        if (_streams.TryGetValue(streamId, out var source))
            try { source.Cancel(); } catch (ObjectDisposedException) { }
    }
    public async Task CancelStreamAsync(string streamId, CancellationToken ct = default)
    {
        _completions.TryGetValue(streamId, out var completion);
        CancelStream(streamId);
        if (completion != null) await completion.Task.WaitAsync(ct);
    }
}
