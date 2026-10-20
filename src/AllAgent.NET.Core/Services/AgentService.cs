using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

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
    private readonly ConversationRuntimePool.Lease _runtimeLease;
    private ConversationRuntimeFactory _runtimes => _runtimeLease.Factory;
    private readonly ConcurrentDictionary<string, byte> _childJobs = new();
    private readonly bool _ownsJobs;
    private int _stopping;
    public object? PromptDiagnostics(Guid conversationId) => _runtimes.Find(conversationId)?.CodeAct.GetPromptDiagnostics();
    public async ValueTask ReleaseRuntimeAsync(Guid conversationId)
    {
        if (IsRunning(conversationId) || Jobs.List.Any(job => job.SessionId == conversationId && BackgroundTasks.IsActive(job.State)))
            throw new InvalidOperationException("Cannot release a running conversation runtime.");
        await _runtimes.ReleaseAsync(conversationId);
    }
    public BackgroundTasks Jobs { get; }
    public bool IsRunning(Guid conversationId) => _conversations.ContainsKey(conversationId);
    /// <summary>Conversations with a run in flight; hosts use it to mark background sessions.</summary>
    public IReadOnlyCollection<Guid> RunningConversations => _conversations.Keys.ToArray();
    public bool OwnsStream(string streamId) => _streams.ContainsKey(streamId);
    /// <summary>Raised when a conversation starts or finishes a run, so hosts can rebroadcast run state.</summary>
    public event Action<Guid>? RunStateChanged;
    public BrowserUseState BrowserUse => _options.BrowserUse;
    /// <summary>Root the runs of this service are confined to; hosts resolve `@` references against it.</summary>
    public string WorkspaceRoot => _options.WorkspaceRoot;
    /// <summary>True while any top-level run is active; hosts use it to block configuration changes mid-run.</summary>
    public bool HasActiveRuns => !_conversations.IsEmpty;
    /// <summary>Optional host hook: registers extra tool sets on each freshly created runtime (plugin tools, MCP resources, …).</summary>
    public Action<AgentRuntime>? RuntimeInitializer { get; set; }
    /// <summary>Host-owned account authentication and provider routing, shared by
    /// browser runs, child runs and bridge model-management requests.</summary>
    public Func<ApiConfig, IAiProvider>? ProviderResolver { get; set; }
    internal IAiProvider ResolveProvider(ApiConfig config) => ProviderResolver?.Invoke(config) ?? _factory.GetProvider(config);
    /// <summary>Tool catalog for the active host configuration: built-ins plus whatever the
    /// runtime initializer and MCP service contribute. Builds a throwaway runtime.</summary>
    public async Task<IReadOnlyList<AgentToolDescriptor>> ListToolDefinitionsAsync(CancellationToken ct = default) => (await ListToolsAsync(ct)).Tools;

    /// <summary>Definitions plus the on-demand surface a fresh runtime applies, so a host listing can tell
    /// which families the model only sees after loading their skill.</summary>
    public async Task<(IReadOnlyList<AgentToolDescriptor> Tools, ToolSurface Surface)> ListToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var runtime = AgentRuntime.CreateCatalog(_options);
        if (_mcp != null) runtime.Tools.Register(new McpToolSet(_mcp));
        RuntimeInitializer?.Invoke(runtime);
        runtime.RefreshToolSurface();
        return (runtime.Tools.Definitions, runtime.CodeAct.Surface);
    }
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(Timeout.InfiniteTimeSpan);
        if (_ownsJobs) await Jobs.DisposeAsync();
    }

    /// <summary>
    /// Cancels every active run and detaches from the shared job list without
    /// disposing it — for hosts (like the terminal's browser workspace) that
    /// pass in a job list they own and keep using afterwards.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _stopping, 1);
        Jobs.Completed -= OnJobCompleted;
        foreach (var id in _streams.Keys) CancelStream(id);
        var ownedChildren = Jobs.List.Where(job => BackgroundTasks.IsActive(job.State) && _childJobs.ContainsKey(job.Id)).ToArray();
        foreach (var child in ownedChildren) Jobs.Cancel(child.Id);
        var completions = Task.WhenAll(_completions.Values.Select(completion => completion.Task).Concat(ownedChildren.Select(child => Jobs.WaitAsync(child.Id))));
        async Task ReleaseWhenDrained() { try { await completions; } finally { await _runtimeLease.DisposeAsync(); } }
        try { await ReleaseWhenDrained().WaitAsync(timeout); }
        catch (TimeoutException) { /* disposal continues after the owned runs drain */ }
    }
    private readonly DataStore _store;
    private readonly AiProviderFactory _factory;
    private readonly AgentOptions _options;
    private readonly ConcurrentDictionary<Guid, ApiConfig> _lastConfig = new();
    private readonly McpToolService? _mcp;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();
    private readonly ConcurrentDictionary<Guid, string> _conversationStreams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _completions = new();
    private readonly ConcurrentDictionary<Guid, byte> _conversations = new();
    /// <summary>Disclosures survive worker replacement but never cross conversation boundaries.</summary>

    /// <summary>The capability record a conversation currently discloses; hosts read it to render state.</summary>
    public ToolSurface SurfaceFor(Guid conversationId) => _runtimes.SurfaceFor(_options.WorkspaceRoot, conversationId);

    public AgentService(DataStore store, AiProviderFactory providerFactory, McpToolService toolService)
        : this(store, providerFactory, new AgentOptions(), toolService) { }
    public AgentService(DataStore store, AiProviderFactory providerFactory, AgentOptions options)
        : this(store, providerFactory, options, null) { }
    public AgentService(DataStore store, AiProviderFactory providerFactory, AgentOptions options, McpToolService? mcp)
    {
        _store = store; _factory = providerFactory;
        _runtimeLease = ConversationRuntimePool.Acquire(store.DataDirectory);
        _ownsJobs = options.BackgroundTasks == null;
        _options = options with { Skills = options.Skills ?? new SkillCatalog(store, options.WorkspaceRoot, options.SharedDirectory) };
        _mcp = mcp; Jobs = options.BackgroundTasks ?? new(); _options.BrowserUse.Load(store);
        Jobs.Completed += OnJobCompleted;
    }

    /// <summary>Fallback composition for hosts that supply no instruction provider: project
    /// instructions. The skill catalog is appended by <see cref="AgentEngine"/> as a tail snapshot.</summary>
    private string BaseInstructions() => ProjectInstructions.Load(_options.WorkspaceRoot);

    /// <summary>
    /// The host provider owns the project instructions; this wrapper only appends the
    /// delegated-role list, so one stable prompt never carries the same catalog twice. The skill
    /// catalog itself is a tail snapshot owned by AgentEngine. A host that supplies no provider
    /// (embedding) receives the fallback text instead.
    /// </summary>
    internal static string ComposeInstructions(string? hostInstructions, string fallbackInstructions, string childAgents) =>
        PromptComposer.Join(string.IsNullOrEmpty(hostInstructions) ? fallbackInstructions : hostInstructions) + "\nAvailable child agents:\n" + childAgents;

    public string StartStream(Guid conversationId, string userMessage, ApiConfig config,
        Func<string, AgentEvent, Task> onEvent, CancellationToken externalCt = default, bool internalMessage = false,
        IReadOnlyList<ImageAttachment>? images = null, string? inputText = null, string? sessionOperation = null,
        bool fromInbox = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        if (!_conversations.TryAdd(conversationId, 0)) throw new InvalidOperationException("This conversation already has an active run.");
        RunStateChanged?.Invoke(conversationId);
        _lastConfig[conversationId] = config;
        var id = Guid.NewGuid().ToString("N");
        var source = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _streams[id] = source; _conversationStreams[conversationId] = id;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _completions[id] = completion;
        _ = Task.Run(async () =>
        {
            AgentDone? terminal = null;
            AgentError? failure = null;
            try
            {
                // Host-owned jobs remain inspectable after the parent turn completes.
                var children = Jobs;
                // The host provider already composes project instructions and the skill catalog
                // (see TerminalApp.Instructions); only a host without a provider falls back to them here,
                // so the same skill list is never appended twice to one system prompt.
                var runtimeOptions = _options with { BackgroundTasks = Jobs, ToolSurface = SurfaceFor(conversationId),
                    InstructionsProvider = () => _options.IsSubAgent || _options.SimpleModeActive || _options.ReadOnlyProvider?.Invoke() == true
                        ? _options.InstructionsProvider?.Invoke() ?? BaseInstructions()
                        : ComposeInstructions(_options.InstructionsProvider?.Invoke(), BaseInstructions(), string.Join("\n", new AgentRoleCatalog(_store).List().Select(r => r.Name + ": " + r.Description))) };
                var runtime = await _runtimes.GetAsync(conversationId, runtimeOptions, initialized =>
                {
                    if (_mcp != null) { initialized.Tools.Register(new McpResourceTools(_mcp)); initialized.Tools.Register(new McpToolSet(_mcp)); }
                    RuntimeInitializer?.Invoke(initialized);
                }, source.Token);
                // MCP resources belong to every host, not only the terminal/Web runtimes.
                if (!_options.IsSubAgent)
                    runtime.Tools.Replace<AgentDelegationTools>(new AgentDelegationTools(children, async (request, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var role = new AgentRoleCatalog(_store).Get(request.Agent);
                        if (role.Execution?.MaxDepth == 0) throw new InvalidOperationException("This role disables child execution.");
                        if (!role.ReadOnly) _options.Checkpoints?.MarkExternalAction();
                        var selected = SubAgentModelSelection.Resolve(config, _store.ApiConfigs,
                            request.Model ?? role.Model ?? _store.GetSetting("subagents:model"), request.Provider ?? role.Provider ?? _store.GetSetting("subagents:provider"));
                        selected = (role.Execution ?? new()).Apply(selected);
                        var child = SubAgentSessions.Resolve(_store, children, conversationId, request, allowRunning: true);
                        var joined = children.TryJoin(child.Id, conversationId, _ =>
                            _store.EnqueueInput(child.Id, request.Prompt, request.Prompt, [], "steer", selected, request.Agent));
                        if (joined != null) return new SubAgentLaunch(joined, Joined: true);
                        child.SubAgentPermission = _options.CurrentPermission;
                        child.SubAgentRoleGrant = role;
                        child.SubAgentHostToolsGrant = _options.HostToolsProvider?.Invoke()?.ToArray();
                        child.SubAgentConfigId = selected.Id; child.ModelId = selected.DefaultModel; child.ProviderId = selected.ProviderId;
                        if (request.SessionId == null) _store.AddConversation(child); else _store.UpdateConversation(child);
                        // Foreground children are owned by the parent turn and cancelled with it; background children outlive the turn and report by notification.
                        var firstRun = true;
                        var jobId = children.Start(request.Description, child.Id, "agent", (job, ct) =>
                        {
                            // A steer resumes the same job; consume pending input instead of repeating the original task.
                            var fromInbox = !firstRun; firstRun = false;
                            return RunChildAsync(child.Id, request.Prompt, selected, ct, fromInbox);
                        }, conversationId,
                            () => _store.GetPendingInputs(child.Id).Any(input => input.Delivery == "steer"),
                            request.Background ? CancellationToken.None : token);
                        if (request.Background) children.NotifyParentOnCompletion(jobId);
                        _childJobs.TryAdd(jobId, 0);
                        return new SubAgentLaunch(jobId);
                    }));
                // Plugin skills and tools may have arrived with the initializer; disclose accordingly.
                runtime.RefreshToolSurface();
                // A session operation (compaction) only asks the model to summarize existing context:
                // it needs no tools, so a missing microVM must not block it.
                var engine = new AgentEngine(_store, new RequestRoutingAiProvider(ResolveProvider), runtime.CodeAct, runtimeOptions);
                await foreach (var evt in engine.RunAsync(conversationId, userMessage, config, source.Token,
                    internalMessage: internalMessage, images: images, fromInbox: fromInbox, inputText: inputText, sessionOperation: sessionOperation))
                {
                    if (evt is AgentDone done) terminal = done;
                    else if (evt is AgentError error) failure = error;
                    else await onEvent(id, evt);
                }
            }
            catch (OperationCanceledException) { terminal = new AgentDone("cancelled"); }
            catch (Exception ex) { failure = new AgentError(ex.Message); terminal = new AgentDone("error"); }
            finally
            {
                _streams.TryRemove(id, out _); _conversations.TryRemove(conversationId, out _); source.Dispose();
                if (_conversationStreams.TryGetValue(conversationId, out var current) && current == id) _conversationStreams.TryRemove(conversationId, out _);
                RunStateChanged?.Invoke(conversationId);
                // A terminal UI event grants permission to send the next turn.
                // Release the run first, and acknowledge cancellation only once
                // both cleanup and terminal notifications have completed.
                try
                {
                    if (failure != null) await onEvent(id, failure);
                    if (terminal != null) await onEvent(id, terminal);
                }
                finally { completion.TrySetResult(); _completions.TryRemove(id, out _); }
            }
        });
        return id;
    }

    private async IAsyncEnumerable<AgentEvent> RunChildAsync(Guid conversationId, string task, ApiConfig config,
        [EnumeratorCancellation] CancellationToken ct, bool fromInbox = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        AgentRoleDefinition CurrentRole() => new AgentRoleCatalog(_store).Get(_store.GetConversation(conversationId)?.AgentRole ?? "general");
        var role = CurrentRole();
        var roleGrant = _store.GetConversation(conversationId)?.SubAgentRoleGrant ?? role;
        var grant = _store.GetConversation(conversationId)?.SubAgentPermission ?? _options.CurrentPermission;
        var settings = role.Execution ?? new SubAgentExecutionSettings();
        var bounded = settings.Apply(_options);
        linked.CancelAfter(bounded.RunTimeout);
        using var slot = await SubAgentConcurrency.AcquireAsync(_store, role.Name, settings.MaxConcurrent, linked.Token);
        var rolePolicy = new SubAgentRolePolicy(() => CurrentRole().ReadOnly, _options.WorkspaceRoot, _options.ExternalDirectoryPermissions, CurrentRole,
            parentTools: _options.HostToolsProvider, grant: roleGrant, parentToolGrant: _store.GetConversation(conversationId)?.SubAgentHostToolsGrant);
        var options = settings.Apply(_options) with
        {
            BackgroundTasks = Jobs,
            // A child discloses for itself: it never inherits the parent's activations.
            ToolSurface = SurfaceFor(conversationId),
            IsSubAgent = true,
            HostToolsProvider = () => SubAgentRolePolicy.IntersectTools(CurrentRole().Tools, roleGrant.Tools),
            ReadOnlyProvider = () => roleGrant.ReadOnly || CurrentRole().ReadOnly || _options.ReadOnlyProvider?.Invoke() == true,
            TaskContractProvider = () => _store.GetConversation(conversationId)?.Delegation,
            ConfigurationProvider = (_, current) => (CurrentRole().Execution ?? new()).Apply(current),
            Permission = role.ReadOnly && _options.CurrentPermission == PermissionMode.Full ? PermissionMode.Ask : _options.CurrentPermission,
            // A child holds the stance it was spawned with; a parent switching to full mid-flight
            // must never widen a delegation, least of all a read-only one.
            PermissionProvider = () => DelegatedPermissions.Restrict(grant, _options.CurrentPermission),
            SimpleModeProvider = null,
            InvocationPolicy = (tool, arguments) => rolePolicy.Validate(tool, arguments) ?? _options.InvocationPolicy?.Invoke(tool, arguments),
            InvocationAuthorization = async (tool, arguments, token) => ( _options.InvocationAuthorization == null ? null : await _options.InvocationAuthorization(tool, arguments, token)) ?? await rolePolicy.AuthorizeAsync(tool, arguments, token),
            Checkpoints = null,
            InstallationApprover = null,
            QuestionHandler = null,
            PlanHandler = null,
            Approver = (request, _) => ValueTask.FromResult(request.Preapproved),
            InstructionsProvider = () => ((CurrentRole().Execution ?? new()).IncludeProjectInstructions ? (_options.InstructionsProvider?.Invoke() ?? BaseInstructions()) : "") +
                "\nYou are a delegated agent. Stay within your assigned task, report evidence and limitations, and do not spawn agents or ask the user. Return missing information and permission blockers to the parent in your final response. Do not bypass host permissions.\n" + CurrentRole().Instructions
        };
        var runtime = await _runtimes.GetAsync(conversationId, options, initialized =>
        {
            if (_mcp != null) { initialized.Tools.Register(new McpResourceTools(_mcp)); initialized.Tools.Register(new McpToolSet(_mcp)); }
            RuntimeInitializer?.Invoke(initialized);
        }, linked.Token);
        runtime.RefreshToolSurface();
        var childProvider = new SubAgentAiProvider(new RequestRoutingAiProvider(ResolveProvider), settings,
            current => SubAgentModelSelection.Resolve(current, _store.ApiConfigs, settings.FallbackModel, settings.FallbackProvider));
        var engine = new AgentEngine(_store, childProvider, runtime.CodeAct, options);
        if (fromInbox)
        {
            await foreach (var evt in engine.RunAsync(conversationId, "Continue pending child input.", config, linked.Token, fromInbox: true)) yield return evt;
            yield break;
        }
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
    /// <summary>Stops the run of a conversation addressed by id — the browser needs this for
    /// sessions running in a workspace other than the one it is currently looking at.</summary>
    public async Task<bool> CancelConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        if (!_conversationStreams.TryGetValue(conversationId, out var streamId)) return false;
        await CancelStreamAsync(streamId, ct);
        return true;
    }

    /// <summary>Optional host hook: a background child finished or a wake-up turn ended; the bridge pushes tasks.updated.</summary>
    public Action<Guid>? TaskRefreshRequested { get; set; }

    /// <summary>Wake an idle parent conversation so a finished background child is read without waiting for the next user message.</summary>
    private void OnJobCompleted(BackgroundTaskInfo job)
    {
        _childJobs.TryRemove(job.Id, out _);
        if (job.ParentSessionId is not { } parent || !job.NotifyParent) return;
        if (!_lastConfig.TryGetValue(parent, out var config)) return;
        if (_conversations.ContainsKey(parent)) return;
        if (_store.GetConversation(parent)?.Revert != null) return;
        TaskRefreshRequested?.Invoke(parent);
        try
        {
            StartStream(parent, "Review the newly delivered delegated task results and continue the existing parent objective. Verify claims before relying on them.",
                config, (_, evt) => { if (evt is AgentDone) TaskRefreshRequested?.Invoke(parent); return Task.CompletedTask; }, internalMessage: true);
        }
        catch (InvalidOperationException) { /* Another run started first; its own drain picks the notification up. */ }
    }
}

public record AgentTokenUsage(long InputTokens, long OutputTokens) : AgentEvent;
