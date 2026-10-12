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
    public ValueTask DisposeAsync() => Jobs.DisposeAsync();
    private readonly DataStore _store;
    private readonly AiProviderFactory _factory;
    private readonly AgentOptions _options;
    private readonly McpToolService? _mcp;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();
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
        _ = Task.Run(async () =>
        {
            try
            {
                // Host-owned jobs remain inspectable after the parent turn completes.
                var children = Jobs;
                await using var runtime = await AgentRuntime.CreateAsync(_options with { BackgroundTasks = Jobs }, source.Token);
                if (_mcp != null) runtime.Tools.Register(new McpToolSet(_mcp));
                if (!_options.IsSubAgent)
                    runtime.Tools.Register(new SubAgentTools(children, (task, model, provider) =>
                    {
                        var selected = SubAgentModelSelection.Resolve(config, _store.ApiConfigs,
                            model ?? (provider == null ? _store.GetSetting("subagents:model") : null),
                            provider ?? _store.GetSetting("subagents:provider"));
                        var child = new Conversation { Title = "子 Agent · " + ToolResult.Clip(task, 40), ModelId = selected.DefaultModel };
                        _store.AddConversation(child);
                        var parentToken = source.Token;
                        return children.Start(task, child.Id, "agent", (job, ct) => RunChildAsync(child.Id, task, selected, parentToken, ct));
                    }));
                var probe = await runtime.ProbeSandboxAsync(source.Token);
                if (!probe.Ok) throw new InvalidOperationException(probe.Error);
                var engine = new AgentEngine(_store, _factory.GetProvider(config), runtime.CodeAct, _options);
                await foreach (var evt in engine.RunAsync(conversationId, userMessage, config, source.Token)) await onEvent(id, evt);
            }
            catch (OperationCanceledException) { await onEvent(id, new AgentDone("cancelled")); }
            catch (Exception ex) { await onEvent(id, new AgentError(ex.Message)); await onEvent(id, new AgentDone("error")); }
            finally { _streams.TryRemove(id, out _); _conversations.TryRemove(conversationId, out _); source.Dispose(); }
        });
        return id;
    }

    private async IAsyncEnumerable<AgentEvent> RunChildAsync(Guid conversationId, string task, ApiConfig config,
        CancellationToken parentToken, [EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, ct);
        var options = _options with
        {
            BackgroundTasks = Jobs,
            IsSubAgent = true,
            Checkpoints = null,
            InstallationApprover = null,
            InstructionsProvider = () => (_options.InstructionsProvider?.Invoke() ?? "") +
                "\nYou are a delegated agent. Stay within your assigned task, report evidence and limitations, and do not spawn agents or bypass host permissions."
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
}
