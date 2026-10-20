using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Localization;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;

namespace AllAgent.NET.Terminal;

// Payloads registered on the bridge by this session.
public sealed record WorkspaceSwitchParams(string Path, bool Create = false);
public sealed record WorkspaceInfo(string Workspace, string DataDirectory, string SharedDirectory, string WorkspacesRoot = "");
public sealed record SessionControlState(string PermissionMode, string PermissionLabel, string SandboxMode, string SandboxEffective, bool Running);
public sealed record SessionPermissionParams(string Mode);
public sealed record SessionSandboxParams(string Mode);
public sealed record PluginToggleParams(string PackageId, bool Enabled);
public sealed record PluginRemoveParams(string PackageId);
public sealed record PluginSearchParams(string Query, int Skip = 0, int Take = 10);
public sealed record PluginRunCommandParams(string Name, List<string> Args);
public sealed record PluginInstallParams(string Id, string Version);
public sealed record PluginImportParams(string FileName, string Base64);
public sealed record HookToggleParams(string Name, bool Enabled);
public sealed record ExportParams(Guid Id);

// Model footer: the plain-text line plugins and account providers register for the bottom of the
// terminal. The browser renders the same items under its composer; the fetch itself is cached in
// ModelFooter so a bridge call never waits on a network round trip.
public sealed record ModelFooterQuery(Guid? ApiConfigId, string? Model);
public sealed record ModelFooterItemInfo(string Label, string Value);
public sealed record ModelFooterSnapshot(IReadOnlyList<ModelFooterItemInfo> Items, bool Refreshing);

// Per-conversation usage the terminal keeps in its sidebar: cache hit rate plus the persisted
// context count, so a reloaded page shows the same numbers instead of losing them until the next turn.
public sealed record ConversationUsageParams(Guid ConversationId);
public sealed record ConversationUsageDto(int? ContextUsedTokens, bool ContextEstimated, double? CacheHitRate,
    long CacheInputTokens, long CachedInputTokens, long CacheWriteInputTokens, long CacheRequests);
public sealed record PluginProviderInfo(string ProviderId);
public sealed record PluginAuthInfo(string Id, string Name);
public sealed record HookInfo(string Name, bool Enabled);
public sealed record PluginsSnapshot(IReadOnlyList<PluginStatus> Plugins, IReadOnlyList<PluginProviderInfo> Providers, IReadOnlyList<PluginAuthInfo> Authentication, IReadOnlyList<HookInfo> Hooks);

// Account login state, mirroring the terminal's /login list (providers + API-key presets).
public sealed record AuthProviderInfo(string Id, string Name, string Kind, bool LoggedIn, string Account, string Message, bool ModelConnectionAvailable, string? InputPrompt = null, string? ProviderId = null, string? ConfigId = null, bool Pending = false);
public sealed record AuthLoginParams(string Id, bool NewAccount = false);
public sealed record AuthInputParams(string Id, string Secret);
public sealed record AuthLogoutParams(string Id);
public sealed record AuthStatusParams(string Id);

// Web command catalog, projected from the terminal's slash commands, plugin commands and skills.
public sealed record CommandInfo(string Text, string Description, string Usage = "", bool Advanced = false, bool IsSkill = false, string Aliases = "");

// Workspace list with session counts. Every conversation belongs to exactly one
// workspace, so the sidebar groups by these entries instead of a flat list.
public sealed record WorkspaceEntry(string Workspace, string Name, string DataDirectory, int SessionCount, bool IsCurrent, int RunningCount = 0);
public sealed record WorkbenchConversation(Guid Id, string Title, string ModelId, string ProviderId, DateTime CreatedAt, DateTime UpdatedAt, string Workspace, string WorkspaceName, string? AgentRole, Guid? ParentId);
/// <summary>A conversation with a run in flight, in any workspace opened this session. State is one of
/// running, awaiting-approval, awaiting-answer, awaiting-plan.</summary>
public sealed record RunningConversation(Guid ConversationId, string Title, string Workspace, string WorkspaceName, string DataDirectory, string State);

// Instruction-skill catalog (SKILL.md), the system the terminal's /skills manages.
public sealed record SkillInfo(string Name, string Description, string Scope, bool Enabled, bool UserInvocable, bool ModelInvocable);
public sealed record SkillCatalogSnapshot(IReadOnlyList<SkillInfo> Skills, IReadOnlyList<string> Diagnostics);
public sealed record SkillNameParams(string Name);
public sealed record SkillToggleParams(string Name, bool Enabled);
public sealed record SkillCreateParams(string Name, string Description, string Instructions, string Scope);
public sealed record PluginSettingsQuery(string PackageId);
public sealed record PluginSettingsSnapshot(string PackageId, IReadOnlyDictionary<string, string> Values);
public sealed record PluginSettingsSave(string PackageId, string Key, string Value);
public sealed record SkillImportParams(string Content, string Scope);
public sealed record SkillInvokeParams(string Name, string Arguments);
public sealed record SkillInvokeResult(string Text, IReadOnlyList<ImageAttachment> Images);
public sealed record WorkspaceCreateParams(string Name);
public sealed record WorkspaceFilesParams(string Query = "", int Limit = 40);
public sealed record OpenPathParams(string Path);

/// <summary>The terminal's own workspace runtime, shared when the browser stays on the same workspace.</summary>
internal sealed record WebUiTerminalHost(
    DataStore Store, McpToolService Mcp, BackgroundTasks Tasks, WorkspaceCheckpoints Checkpoints, SkillCatalog Catalog,
    string Workspace, string DataDirectory);

/// <summary>Live terminal controls the browser may read and change; the terminal stays the single source of truth.</summary>
internal sealed record SessionControls(
    Func<bool> PlanMode,
    Func<bool> FullAccess,
    Func<SessionControlState> Snapshot,
    Func<string, Task> SetPermission,
    Func<string, Task> SetSandbox);

/// <summary>
/// Hosts the browser workspace for the terminal: serves the built web assets
/// over loopback HTTP, bridges browser requests onto the same handlers the
/// desktop WebView uses, and runs agent turns for the browser. The session
/// shares conversation storage and background jobs with the terminal when both
/// point at the same workspace. MCP connections belong to this session and are
/// opened explicitly; stopping the Web UI releases them.
/// </summary>
internal sealed class WebUiSession : IAsyncDisposable
{
    /// <summary>One browser workspace: its own store, jobs, agent service and bridge bindings. Bundles
    /// are kept alive after the browser switches away, so a run in another workspace finishes instead
    /// of being killed by the switch.</summary>
    private sealed record Bundle(
        DataStore Store, McpToolService Mcp, BackgroundTasks Tasks, WorkspaceCheckpoints Checkpoints, SkillCatalog Catalog,
        AgentService Agents, BridgeDispatcher Bindings, string Workspace, string DataDirectory, bool Owned)
    {
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        /// <summary>Model footer cache for this workspace's providers; disposed with the bundle.</summary>
        public ModelFooter? Footer { get; set; }
    }

    /// <summary>Idle workspaces kept ready for a switch back; the current one is always kept.</summary>
    private const int RetainedWorkspaces = 4;
    private const string BundleSlot = "bundle";
    private const string PluginWebSlot = "plugin-web";

    private readonly BridgeDispatcher _dispatcher = new();
    private readonly BridgeEventPusher _pusher = new();
    private readonly WebTerminalManager _terminals;
    private readonly WebApprovals _approvals;
    private readonly AiProviderFactory _factory;
    private readonly PluginHost _plugins;
    private readonly WebUiTerminalHost _terminal;
    private readonly Func<AgentOptions> _baseOptions;
    private readonly string _sharedDirectory;
    private readonly int _backgroundConcurrency;
    private readonly Func<ApiConfig, IAiProvider>? _providerResolver;
    private readonly SessionControls? _controls;
    private readonly Dictionary<string, Bundle> _bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bundlesGate = new();
    private WebUiServer _server = null!;
    private PluginAssetServer? _pluginAssets;
    private Bundle _current = null!;
    internal bool UseTerminalPrompts { get; set; }

    public string Url => _server.Url;
    public int Port => _server.Port;
    public int ConnectedClients => _server.ConnectedClients;

    /// <summary>The terminal changed the shared language setting: apply it to every open page.</summary>
    public void PushSettingsChanged()
    {
        var store = _current?.Store;
        if (store == null) return;
        var language = LanguageSetting.Current(store);
        _pusher.Push(JsonSerializer.Serialize(new
        {
            type = "settings.changed",
            language,
            languageResolved = HostText.ResolvedCode,
        }, JsonOptions.Default));
    }

    /// <summary>Launch through the workspace's normal bridge so persistence, replay, approvals and
    /// background lifetime are identical to an ordinary chat, without changing the visible workspace.</summary>
    internal async Task<PluginTaskResult> StartPluginTaskAsync(PluginTaskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 240 || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 250_000 || request.Message?.Length > 20_000)
            throw new ArgumentException(L.Pick("插件任务标题或提示词无效。", "The plugin task title or prompt is invalid."));
        var bundle = CreateBundle(request.Workspace);
        var config = bundle.Store.ApiConfigs.FirstOrDefault(c => c.IsEnabled && c.Id.ToString() == bundle.Store.GetSetting("terminal:lastConfig"))
            ?? bundle.Store.ApiConfigs.FirstOrDefault(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.DefaultModel))
            ?? throw new InvalidOperationException(L.Pick("请先在模型与 API 中配置并启用一个模型。", "Configure and enable a model in Models & API first."));
        var conversation = new Conversation { Title = request.Title, WorkspaceRoot = bundle.Workspace, ModelId = config.DefaultModel, ProviderId = config.ProviderId };
        bundle.Store.AddConversation(conversation);
        try
        {
            var message = string.IsNullOrWhiteSpace(request.Message) ? request.Title : request.Message;
            var payload = JsonSerializer.SerializeToElement(new StartStreamParams(conversation.Id, message, config.Id, Prompt: request.Prompt), JsonOptions.Default);
            var stream = (string)(await bundle.Bindings.Handlers["chat.startStream"](payload, ct))!;
            return new(bundle.Workspace, conversation.Id, stream);
        }
        catch { bundle.Store.DeleteConversation(conversation.Id); throw; }
    }

    public static string DefaultWebRoot => Path.Combine(AppContext.BaseDirectory, "web");

    public static bool AssetsAvailable => File.Exists(Path.Combine(DefaultWebRoot, "index.html"));

    private WebUiSession(WebUiTerminalHost terminal, Func<AgentOptions> baseOptions, AiProviderFactory factory, PluginHost plugins,
        string sharedDirectory, int backgroundConcurrency, Func<ApiConfig, IAiProvider>? providerResolver, SessionControls? controls)
    {
        _terminal = terminal;
        _baseOptions = baseOptions;
        _factory = factory;
        _plugins = plugins;
        _sharedDirectory = sharedDirectory;
        _backgroundConcurrency = backgroundConcurrency;
        _providerResolver = providerResolver;
        _controls = controls;
        // Integrated terminals are session-level: they survive page reloads, view switches and
        // workspace switches, and are killed together with the Web UI session.
        _terminals = new WebTerminalManager(_pusher, () => _current.Workspace);
        _approvals = new WebApprovals(_dispatcher, _pusher)
        {
            PlanMode = () => controls?.PlanMode() ?? false,
            FullAccess = () => controls?.FullAccess() ?? false,
        };
        _approvals.OnPlanApproved = async ct =>
        {
            if (_controls == null) throw new InvalidOperationException(L.Pick("当前宿主不支持计划审批。", "This host does not support plan approval."));
            await _controls.SetPermission("full");
            PushControls();
        };
        _approvals.Changed += PushRuns;
    }

    public static WebUiSession Start(WebUiTerminalHost terminal, Func<AgentOptions> baseOptions, AiProviderFactory factory, PluginHost plugins,
        string sharedDirectory, int backgroundConcurrency, Func<ApiConfig, IAiProvider>? providerResolver = null, SessionControls? controls = null, int port = 0)
    {
        var session = new WebUiSession(terminal, baseOptions, factory, plugins, sharedDirectory, backgroundConcurrency, providerResolver, controls);
        session.RegisterStaticHandlers();
        session.SetCurrent(session.CreateBundle(terminal.Workspace));
        // Plugin web payloads live on their own loopback origin so sandboxed frames cannot reach
        // the app page or the /bridge socket.
        session._pluginAssets = PluginAssetServer.TryStart(plugins, DefaultWebRoot);
        session._server = WebUiServer.Start(session._dispatcher, session._pusher, DefaultWebRoot, port);
        session._approvals.HasClients = () => session._server.ConnectedClients > 0;
        // A reloaded page starts with an empty prompt list, so hand it the prompts still waiting.
        session._server.ClientConnected = session._approvals.ReplayPending;
        plugins.Changed += session.SyncPluginWeb;
        plugins.WebEvents.Pushed += session.OnPluginWebPush;
        session.SyncPluginWeb();
        return session;
    }

    public static bool OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string StatusText() => L.Pick(
        $"工作区：{_current.Workspace}\n数据目录：{_current.DataDirectory}\n打开的工作区：{_bundles.Count} 个 · 后台运行 {RunningList().Count} 个会话\n地址：{_server.Url}\n浏览器连接：{_server.ConnectedClients} 个\n停止：/web-ui stop",
        $"Workspace: {_current.Workspace}\nData directory: {_current.DataDirectory}\nOpen workspaces: {_bundles.Count} · {RunningList().Count} background sessions\nURL: {_server.Url}\nBrowser connections: {_server.ConnectedClients}\nStop: /web-ui stop");

    /// <summary>Rebuilds the browser runtime so a terminal-side change (plan mode, sandbox, …) takes effect immediately.</summary>
    public async Task RefreshRuntimeAsync()
    {
        if (_current.Agents.HasActiveRuns || _current.Tasks.Active) return;
        var previous = _current;
        lock (_bundlesGate) _bundles.Remove(previous.DataDirectory);
        SetCurrent(CreateBundle(previous.Workspace));
        await DisposeBundleAsync(previous);
    }

    public async ValueTask DisposeAsync()
    {
        _plugins.Changed -= SyncPluginWeb;
        _plugins.WebEvents.Pushed -= OnPluginWebPush;
        await _terminals.DisposeAsync();
        if (_pluginAssets != null) await _pluginAssets.DisposeAsync();
        await _server.DisposeAsync();
        Bundle[] bundles;
        lock (_bundlesGate) { bundles = _bundles.Values.ToArray(); _bundles.Clear(); }
        foreach (var bundle in bundles) await DisposeBundleAsync(bundle);
    }

    // ── Bundles ───────────────────────────────────────────────────────────────

    /// <summary>Makes <paramref name="bundle"/> the workspace the bridge answers from. Earlier bundles stay
    /// alive so their runs and background jobs keep going.</summary>
    private void SetCurrent(Bundle bundle)
    {
        _current = bundle;
        bundle.LastUsed = DateTime.UtcNow;
        _dispatcher.Install(BundleSlot, bundle.Bindings.Handlers);
        _approvals.Checkpoints = bundle.Checkpoints;
        PushRuns();
    }

    /// <summary>The conversation behind every run in flight, across every workspace opened this session.</summary>
    private List<RunningConversation> RunningList()
    {
        Bundle[] bundles;
        lock (_bundlesGate) bundles = _bundles.Values.ToArray();
        var awaiting = _approvals.PendingConversations().ToDictionary(item => item.Conversation, item => item.Kind);
        var runs = new List<RunningConversation>();
        foreach (var bundle in bundles)
            foreach (var id in bundle.Agents.RunningConversations)
            {
                var state = awaiting.TryGetValue(id, out var kind)
                    ? kind switch
                    {
                        PendingPromptKind.Plan => "awaiting-plan",
                        PendingPromptKind.Question => "awaiting-answer",
                        _ => "awaiting-approval"
                    }
                    : "running";
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(bundle.Workspace));
                runs.Add(new RunningConversation(id, bundle.Store.GetConversation(id)?.Title ?? L.Pick("会话", "Conversation"), bundle.Workspace, string.IsNullOrEmpty(name) ? bundle.Workspace : name, bundle.DataDirectory, state));
            }
        return runs;
    }

    private void PushRuns() => _pusher.Push(JsonSerializer.Serialize(new { type = "runs.changed", conversations = RunningList() }, JsonOptions.Default));

    /// <summary>Releases workspaces nobody is looking at once too many are open; the current one always stays.</summary>
    private async Task EvictIdleAsync()
    {
        var doomed = new List<Bundle>();
        lock (_bundlesGate)
        {
            var idle = _bundles.Values
                .Where(bundle => !ReferenceEquals(bundle, _current) && !bundle.Agents.HasActiveRuns && !bundle.Tasks.Active)
                .OrderByDescending(bundle => bundle.LastUsed)
                .Skip(RetainedWorkspaces - 1)
                .ToArray();
            foreach (var bundle in idle) { _bundles.Remove(bundle.DataDirectory); doomed.Add(bundle); }
        }
        foreach (var bundle in doomed) await DisposeBundleAsync(bundle);
    }

    private Bundle CreateBundle(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        if (!Directory.Exists(full)) throw new InvalidOperationException(L.Pick("工作区目录不存在：", "The workspace directory does not exist: ") + full);
        // A bundle is identified by the data directory it owns; the terminal's own workspace
        // shares the terminal's store, jobs and checkpoints instead of opening a second copy.
        var shared = string.Equals(full, _terminal.Workspace, StringComparison.OrdinalIgnoreCase);
        var dataDirectory = shared ? _terminal.DataDirectory : LegacyDataPaths.ProjectDirectory(full);
        lock (_bundlesGate)
        {
            if (_bundles.TryGetValue(dataDirectory, out var cached)) { cached.LastUsed = DateTime.UtcNow; return cached; }
            var bundle = shared ? CreateSharedBundle(full, dataDirectory) : CreateOwnedBundle(full, dataDirectory);
            _bundles[dataDirectory] = bundle;
            return bundle;
        }
    }

    private Bundle CreateSharedBundle(string full, string dataDirectory)
    {
        // Web connections belong to the explicit Web UI lifetime. Do not
        // start services on page open or retain them in the terminal on stop.
        var webMcp = new McpToolService(_terminal.Store, new SkillExecutor()) { AuthenticationResolver = _terminal.Mcp.AuthenticationResolver };
        return BuildBundle(_terminal.Store, webMcp, _terminal.Tasks, _terminal.Checkpoints, _terminal.Catalog, full, dataDirectory, owned: false);
    }

    private Bundle CreateOwnedBundle(string full, string dataDirectory)
    {
        DataDirectoryLease.EnsureNoLegacyWriter(dataDirectory);
        ProjectDataDirectory.Register(full, dataDirectory);
        var store = new DataStore(dataDirectory, false, _sharedDirectory);
        store.ImportProjectData(_sharedDirectory, full);
        var mcp = new McpToolService(store, new SkillExecutor()) { AuthenticationResolver = _terminal.Mcp.AuthenticationResolver };
        var tasks = new BackgroundTasks(dataDirectory, _backgroundConcurrency);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(full), dataDirectory);
        checkpoints.RecoverRevert(store);
        var catalog = new SkillCatalog(store, full, _sharedDirectory);
        catalog.SeedBundled();
        return BuildBundle(store, mcp, tasks, checkpoints, catalog, full, dataDirectory, owned: true);
    }

    private Bundle BuildBundle(DataStore store, McpToolService mcp, BackgroundTasks tasks, WorkspaceCheckpoints checkpoints,
        SkillCatalog catalog, string workspace, string dataDirectory, bool owned)
    {
        var agents = BuildAgents(store, mcp, tasks, checkpoints, catalog, workspace, owned);
        var bundle = new Bundle(store, mcp, tasks, checkpoints, catalog, agents, new BridgeDispatcher(), workspace, dataDirectory, owned);
        // Live run state feeds the sidebar markers and the "other sessions running" dock.
        agents.RunStateChanged += _ => PushRuns();
        var registrar = new BridgeHandlerRegistrar(bundle.Bindings, store, agents, _factory, _pusher, mcp);
        registrar.RegisterAll();
        RegisterBundleHandlers(bundle, registrar);
        return bundle;
    }

    private AgentService BuildAgents(DataStore store, McpToolService mcp, BackgroundTasks tasks, WorkspaceCheckpoints checkpoints,
        SkillCatalog catalog, string workspace, bool owned)
    {
        var options = _baseOptions() with
        {
            WorkspaceRoot = workspace,
            BackgroundTasks = tasks,
            Checkpoints = checkpoints,
            Skills = catalog,
            Approver = (request, ct) => _server?.ConnectedClients > 0
                ? _approvals.ApproveAsync(request, ct)
                : UseTerminalPrompts ? _baseOptions().Approver?.Invoke(request, ct) ?? ValueTask.FromResult(false) : ValueTask.FromResult(false),
            QuestionHandler = (question, ct) => _server?.ConnectedClients > 0
                ? _approvals.AskAsync(question, ct)
                : UseTerminalPrompts ? _baseOptions().QuestionHandler?.Invoke(question, ct) ?? Task.FromResult<string?>(null) : Task.FromResult<string?>(null),
            PlanHandler = (plan, ct) => _server?.ConnectedClients > 0
                ? _approvals.ProposePlanAsync(plan, ct)
                : UseTerminalPrompts ? _baseOptions().PlanHandler?.Invoke(plan, ct) ?? Task.FromResult(new PlanDecision(false, "Plan approval is unavailable."))
                    : Task.FromResult(new PlanDecision(false, "No browser is connected; stay in plan mode.")),
            InstructionsProvider = () => TerminalApp.Instructions(workspace, catalog, _approvals.PlanMode?.Invoke() == true),
        };
        var agents = new AgentService(store, _factory, options, mcp);
        agents.ProviderResolver = _providerResolver;
        agents.RuntimeInitializer = runtime =>
        {
            // MCP resource tools are registered by AgentService itself for every host.
            // Only explicitly workspace-independent plugin tools may cross into another
            // workspace; tools capturing the terminal store stay in its own bundle.
            runtime.Tools.Register(owned ? _plugins.WorkspaceIndependentToolSet() : _plugins.ToolSet());
        };
        return agents;
    }

    private static async Task DisposeBundleAsync(Bundle bundle)
    {
        bundle.Footer?.Dispose();
        await bundle.Agents.ShutdownAsync(TimeSpan.FromSeconds(5));
        await bundle.Mcp.DisposeAsync();
        if (!bundle.Owned) return;
        await bundle.Tasks.DisposeAsync();
        bundle.Store.Dispose();
    }

    // ── Bridge handlers ───────────────────────────────────────────────────────

    private void RegisterStaticHandlers()
    {
        var installer = new NuGetPluginInstaller(_sharedDirectory);
        _dispatcher.Register<WorkspaceSwitchParams, WorkspaceInfo>("workspace.switch", (parameters, _) => SwitchWorkspaceAsync(parameters.Path, parameters.Create));
        // Named workspaces live in the documents folder, so the browser never has to
        // type a path: 文档\AllAgent.NET\<名字>.
        _dispatcher.Register<WorkspaceCreateParams, WorkspaceInfo>("workspace.create", (parameters, _) =>
        {
            var name = (parameters.Name ?? string.Empty).Trim();
            if (name.Length == 0) throw new ArgumentException(L.Pick("请输入工作区名称。", "Enter a workspace name."));
            if (name.Length > 64) throw new ArgumentException(L.Pick("工作区名称最长 64 个字符。", "The workspace name can be at most 64 characters."));
            if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException(L.Pick("名称不能包含 \\ / : * ? \" < > | 等字符。", "The name cannot contain \\ / : * ? \" < > | or similar characters."));
            var directory = Path.Combine(WorkspacesRoot, name);
            Directory.CreateDirectory(directory);
            return SwitchWorkspaceAsync(directory, false);
        });
        _dispatcher.Register<WorkspaceInfo>("workspace.get", _ => Task.FromResult(Info()));
        _dispatcher.Register<List<WorkspaceInfo>>("workspace.recent", _ => Task.FromResult(Recent()));
        // `@`-reference completion in the browser composer: workspace-relative paths only.
        _dispatcher.Register<WorkspaceFilesParams, IReadOnlyList<WorkspaceFileEntry>>("workspace.files", (parameters, _) =>
            Task.FromResult(WorkspaceFiles.Match(_current.Workspace, parameters.Query, parameters.Limit)));
        // A file a message points at has no page route, so the host opens it: previewable types in
        // a browser window, the rest in Explorer, instead of the browser navigating to a 404.
        _dispatcher.Register<OpenPathParams, bool>("shell.openPath", (parameters, _) => Task.FromResult(OpenPath(parameters.Path)));
        _dispatcher.Register<PluginsSnapshot>("plugins.list", _ => Task.FromResult(PluginSnapshot()));
        // Plugin web extensions: the contribution snapshot plus the per-plugin soft switch. Bridge
        // methods contributed by plugins live in the plugin-web slot and are re-synced on change.
        _dispatcher.Register<PluginWebSnapshot>("web.contributions", _ => Task.FromResult(_plugins.WebSnapshot(_pluginAssets?.BaseUrl)));
        _dispatcher.Register<PluginToggleParams, bool>("web.setEnabled", (parameters, _) => { _plugins.SetWebEnabled(parameters.PackageId, parameters.Enabled); return Task.FromResult(true); });
        _dispatcher.Register<PluginSettingsQuery, PluginSettingsSnapshot>("web.settings.get", (parameters, _) =>
            Task.FromResult(new PluginSettingsSnapshot(parameters.PackageId, _plugins.SettingsSnapshot(parameters.PackageId))));
        _dispatcher.Register<PluginSettingsSave, bool>("web.settings.set", (parameters, _) =>
            Task.FromResult(_plugins.SetSetting(parameters.PackageId, parameters.Key, parameters.Value)));
        _dispatcher.Register<PluginToggleParams, bool>("plugins.setEnabled", async (parameters, _) => { await _plugins.SetEnabledAsync(parameters.PackageId, parameters.Enabled); return true; });
        _dispatcher.Register<PluginRemoveParams, bool>("plugins.remove", async (parameters, _) => { await _plugins.RemoveAsync(parameters.PackageId); return true; });
        _dispatcher.Register<PluginSearchParams, PluginSearchResult>("plugins.search", (parameters, ct) => installer.SearchAsync(parameters.Query, parameters.Skip, parameters.Take, ct));
        _dispatcher.Register<PluginInstallParams, InstalledPlugin>("plugins.install", async (parameters, ct) => await Adopt(await installer.InstallAsync(parameters.Id, parameters.Version, ct), ct));
        _dispatcher.Register<PluginRunCommandParams, string>("plugins.runCommand", async (parameters, ct) =>
        {
            var command = _plugins.Commands.SingleOrDefault(c => c.Name == parameters.Name) ?? throw new ArgumentException(L.Pick("插件命令不存在；请先在插件页启用对应插件。", "Plugin command not found; enable the plugin on the Plugins page first."));
            return ToolResult.Clip(await command.Execute(parameters.Args.ToArray(), ct), 16000);
        });
        _dispatcher.Register<PluginImportParams, InstalledPlugin>("plugins.import", async (parameters, ct) => await Adopt(await installer.ImportAsync(WriteImportFile(parameters), ct), ct));
        _dispatcher.Register<HookToggleParams, bool>("plugins.setHookEnabled", (parameters, _) => { _plugins.SetHookEnabled(parameters.Name, parameters.Enabled); return Task.FromResult(true); });
        // PowerShell 能力商店: PSGallery modules land in the shared directory the terminal's
        // /powershell panel manages, so this is host-level like plugins rather than workspace data.
        new PowerShellStoreBridge(new PowerShellModuleStore(_sharedDirectory, _baseOptions().PowerShellPath)).Register(_dispatcher);
        // Integrated terminal: ConPTY sessions are host-level (see the field comment) and stream
        // terminal.output / terminal.exit frames to every connected page.
        new TerminalBridge(_terminals).Register(_dispatcher);
        _dispatcher.Register<SessionControlState>("session.controls", _ => Task.FromResult(ControlState()));
        // Runs in flight across every workspace opened this session, so the sidebar can mark
        // background sessions and the dock can jump straight to them.
        _dispatcher.Register<List<RunningConversation>>("conversations.running", _ => Task.FromResult(RunningList()));
        _dispatcher.Register<GetMessagesParams, bool>("conversations.cancelRun", async (parameters, ct) =>
        {
            Bundle[] bundles;
            lock (_bundlesGate) bundles = _bundles.Values.ToArray();
            foreach (var bundle in bundles)
                if (bundle.Agents.IsRunning(parameters.ConversationId))
                    return await bundle.Agents.CancelConversationAsync(parameters.ConversationId, ct);
            return false;
        });
        _dispatcher.Register<SessionPermissionParams, SessionControlState>("session.setPermission", async (parameters, _) =>
        {
            if (_controls == null) throw new InvalidOperationException(L.Pick("当前宿主不支持权限切换。", "This host does not support switching permissions."));
            await _controls.SetPermission(parameters.Mode);
            PushControls();
            return ControlState();
        });
        _dispatcher.Register<SessionSandboxParams, SessionControlState>("session.setSandbox", async (parameters, _) =>
        {
            if (_controls == null) throw new InvalidOperationException(L.Pick("当前宿主不支持沙箱切换。", "This host does not support switching the sandbox."));
            await _controls.SetSandbox(parameters.Mode);
            return ControlState();
        });
    }

    /// <summary>Methods that answer from one specific workspace. They are installed as a set when that
    /// workspace becomes current, so a browser tab that switches back finds its journal still there.</summary>
    private void RegisterBundleHandlers(Bundle bundle, BridgeHandlerRegistrar registrar)
    {
        var bindings = bundle.Bindings;
        // File bubble (read-only) and Git panel: both are bound to this workspace and switch with it.
        new WorkspacePreviewBridge(bundle.Workspace).Register(bindings);
        new GitBridge(bundle.Workspace, _pusher, (prompt, apiConfigId, ct) => CompleteWithModelAsync(bundle, prompt, apiConfigId, ct)).Register(bindings);
        // The terminal resolves account-backed credentials; prefer it over the plain provider factory.
        if (_providerResolver != null)
        {
            bindings.Register<TestApiConfigParams, ApiConfigDto>("apiConfigs.refreshModels", async (parameters, ct) =>
            {
                var config = bundle.Store.GetApiConfig(parameters.Id) ?? throw new Exception("ApiConfig not found");
                var models = await _providerResolver(config).ListModelsAsync(config, ct);
                if (models.Count == 0) throw new InvalidOperationException(L.Pick("接口返回了空模型列表，已有列表未修改。", "The endpoint returned an empty model list; the existing list was not changed."));
                config.Models = models.Select(model => model.Id).ToList();
                ModelCatalog.Remember(config, models);
                bundle.Store.UpdateApiConfig(config);
                return BridgeHandlerRegistrar.ToDto(config);
            });
        }
        bindings.Register<ExportParams, string>("conversations.export", (parameters, _) =>
        {
            var conversation = bundle.Store.GetConversation(parameters.Id) ?? throw new Exception("Conversation not found");
            return Task.FromResult(SessionTranscript.Format(conversation));
        });
        // Child sessions belong to the Tasks view; the sidebar shows top-level
        // conversations of the active workspace, each tagged with its workspace.
        bindings.Register<List<WorkbenchConversation>>("conversations.list", _ => Task.FromResult(WorkbenchList(bundle)));
        bindings.Register<List<WorkspaceEntry>>("workspaces.list", _ => Task.FromResult(WorkspaceList()));
        // Native folder dialog on the host. Non-blocking: the dialog runs on its own
        // STA thread and the pick arrives as a `workspace.folderPicked` event, so the
        // bridge stays responsive while the user browses folders.
        bindings.Register<bool>("workspace.pickFolder", _ =>
        {
            if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0) throw new InvalidOperationException(L.Pick("文件夹选择窗口已经打开。", "A folder picker is already open."));
            Task.Run(() =>
            {
                string? path = null;
                string? error = null;
                try { path = NativeFolderPicker.Pick(L.Pick("选择工作区目录", "Choose a workspace directory")); }
                catch (Exception ex) { error = ex.Message; }
                Interlocked.Exchange(ref _picking, 0);
                if (path == null && error == null) error = NativeFolderPicker.LastError;
                try
                {
                    var payload = JsonSerializer.Serialize(new { type = "workspace.folderPicked", path, error = path == null ? error : null }, JsonOptions.Default);
                    _pusher.Push(payload);
                    Console.Error.WriteLine($"[WebUi] folder pick result pushed: path={path ?? "<cancel>"} error={error ?? "<none>"}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WebUi] folder pick push failed: {ex.Message}");
                }
            });
            return Task.FromResult(true);
        });
        // The browser workspace manages the real instruction-skill catalog, not the
        // legacy store entries the desktop bridge serves.
        var catalog = bundle.Catalog;
        bindings.Register<SkillCatalogSnapshot>("skills.list", _ => Task.FromResult(new SkillCatalogSnapshot(
            catalog.List().Select(skill => new SkillInfo(skill.Name, skill.Description, skill.Scope, skill.Enabled, skill.UserInvocable, skill.ModelInvocable)).ToArray(),
            catalog.Diagnostics.ToArray())));
        bindings.Register<SkillNameParams, string>("skills.body", (parameters, _) => Task.FromResult(catalog.Get(parameters.Name).Instructions));
        bindings.Register<SkillToggleParams, bool>("skills.setEnabled", (parameters, _) => { catalog.Enable(parameters.Name, parameters.Enabled); return Task.FromResult(true); });
        bindings.Register<SkillNameParams, bool>("skills.remove", (parameters, _) => { catalog.Remove(parameters.Name); return Task.FromResult(true); });
        bindings.Register<SkillCreateParams, bool>("skills.create", (parameters, _) =>
        {
            catalog.Create(parameters.Name, parameters.Description, parameters.Instructions, string.IsNullOrWhiteSpace(parameters.Scope) ? "user" : parameters.Scope);
            return Task.FromResult(true);
        });
        bindings.Register<SkillImportParams, bool>("skills.importText", (parameters, _) =>
        {
            ImportSkillText(catalog, parameters.Content, parameters.Scope);
            return Task.FromResult(true);
        });
        // User-invocable skills mirror the terminal's default slash branch exactly
        // (PromptReferences.ExpandSkillAsync): references in the arguments expand first,
        // then the skill instructions are injected around them.
        bindings.Register<SkillInvokeParams, SkillInvokeResult>("skills.invoke", async (parameters, ct) =>
        {
            var input = await PromptReferences.ExpandWithImagesAsync(parameters.Arguments ?? "", bundle.Workspace, ct, allowExplicitExternalImages: true);
            return new SkillInvokeResult(catalog.Invoke(parameters.Name, input.Text), input.Images);
        });

        // Account login: the terminal's /login list, driven from the browser. OAuth opens the
        // system browser on the host and reports back through the `auth.changed` event.
        // Accounts (OAuth profiles, Codex tokens) live in the shared data directory — the same vault
        // the terminal's /login writes — so the browser sees the same session and quota line.
        var auth = new WebAuthService(bundle.Store, _sharedDirectory, _plugins, _pusher, () => bundle.Agents.HasActiveRuns || bundle.Tasks.Active);
        bindings.Register<List<AuthProviderInfo>>("auth.providers", (ct) => auth.ListAsync(ct));
        bindings.Register<AuthLoginParams, bool>("auth.login", (parameters, _) => Task.FromResult(auth.Login(parameters.Id, parameters.NewAccount)));
        bindings.Register<AuthInputParams, AuthProviderInfo>("auth.loginInput", (parameters, ct) => auth.LoginWithInputAsync(parameters.Id, parameters.Secret, ct));
        bindings.Register<AuthLogoutParams, AuthProviderInfo>("auth.logout", (parameters, ct) => auth.LogoutAsync(parameters.Id, ct));
        bindings.Register<AuthStatusParams, AuthProviderInfo>("auth.status", (parameters, ct) => auth.StatusAsync(parameters.Id, ct));

        // Bottom-of-composer footer: the same contributions the terminal renders under its input
        // (built-in account quota plus plugin providers such as Codex). Plugin fetches hit the
        // network, so ModelFooter caches them for a minute and reports when a refresh is in flight;
        // the browser polls again shortly instead of blocking on the request.
        var footer = bundle.Footer = new ModelFooter(() => auth.FooterProviders);
        bindings.Register<ModelFooterQuery, ModelFooterSnapshot>("session.footer", (parameters, _) =>
        {
            var config = parameters.ApiConfigId is { } id ? bundle.Store.GetApiConfig(id) : null;
            if (config == null) return Task.FromResult(new ModelFooterSnapshot([], false));
            // Providers only read identity fields: which provider/account the model belongs to and
            // the model id used for per-model quota buckets.
            var probe = new ApiConfig
            {
                ProviderId = config.ProviderId,
                DefaultModel = string.IsNullOrWhiteSpace(parameters.Model) ? config.DefaultModel : parameters.Model!,
                AuthenticationProviderId = config.AuthenticationProviderId,
            };
            var snapshot = footer.ReadSnapshot(probe);
            return Task.FromResult(new ModelFooterSnapshot(
                snapshot.Items.Select(item => new ModelFooterItemInfo(item.Label, item.Value)).ToArray(), snapshot.Refreshing));
        });

        // Cache hit rate and the persisted context count for one conversation. The hit rate matches
        // the terminal sidebar: cached prompt tokens over all prompt tokens the provider reported.
        bindings.Register<ConversationUsageParams, ConversationUsageDto>("conversations.usage", (parameters, _) =>
        {
            var conversation = bundle.Store.GetConversation(parameters.ConversationId) ?? throw new Exception("Conversation not found");
            // A recorded count belongs to the model that produced it; switching models invalidates it.
            var recorded = conversation.ContextUsedTokens is not null && conversation.ContextUsageModel == conversation.ModelId;
            var hitRate = conversation is { CacheUsageRequests: > 0, CacheInputTokens: > 0 }
                ? Math.Min(conversation.CachedInputTokens, conversation.CacheInputTokens) * 100.0 / conversation.CacheInputTokens
                : (double?)null;
            return Task.FromResult(new ConversationUsageDto(
                recorded ? conversation.ContextUsedTokens : null,
                recorded && conversation.ContextUsageEstimated,
                hitRate,
                conversation.CacheInputTokens, conversation.CachedInputTokens, conversation.CacheWriteInputTokens, conversation.CacheUsageRequests));
        });

        // Slash command catalog for the web command palette: built-ins plus plugin commands
        // and user-invocable skills — the same sources that feed the terminal's hint panel.
        bindings.Register<List<CommandInfo>>("commands.list", _ => Task.FromResult(CommandList(bundle)));

        // Tool catalog with plugin attribution on top of the shared host + MCP listing.
        var pluginOwners = _plugins.PluginTools.ToDictionary(item => item.Tool.Name, item => item.PackageId, StringComparer.Ordinal);
        bindings.Register<ToolsSnapshotDto>("tools.list", async (ct) =>
        {
            var snapshot = await registrar.ListToolsAsync(ct);
            if (pluginOwners.Count == 0) return snapshot;
            return snapshot with { Tools = snapshot.Tools.Select(tool => pluginOwners.TryGetValue(tool.Name, out var packageId) ? tool with { Source = "plugin", Owner = packageId } : tool).ToArray() };
        });
    }

    /// <summary>One-shot completion for the Git panel's AI commit-message drafting. The config the
    /// page explicitly requests (the user's selected model) wins; otherwise mirror the agent's
    /// choice (terminal's last used → first enabled).</summary>
    private async Task<string> CompleteWithModelAsync(Bundle bundle, string prompt, string? apiConfigId, CancellationToken ct)
    {
        var configs = bundle.Store.ApiConfigs.Where(item => item.IsEnabled).ToList();
        var config = (apiConfigId is { Length: > 0 } requested ? configs.FirstOrDefault(item => item.Id.ToString() == requested) : null)
            ?? configs.FirstOrDefault(item => item.Id.ToString() == bundle.Store.GetSetting("terminal:lastConfig"))
            ?? configs.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.DefaultModel))
            ?? throw new InvalidOperationException(L.Pick("请先在「模型与 API」中配置并启用一个模型，再使用 AI 生成提交信息。", "Configure and enable a model in Models & API before generating a commit message."));
        IAiProvider provider;
        try
        {
            provider = _providerResolver?.Invoke(config) ?? _factory.GetProvider(config);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("Unknown AI provider", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(L.Pick($"模型配置「{config.DisplayName}」的插件提供商「{config.ProviderId}」当前不可用（插件未启用或未登录）。请在设置里换一个模型，或启用对应插件后重试。", $"The plugin provider \"{config.ProviderId}\" for model configuration \"{config.DisplayName}\" is unavailable (the plugin is disabled or not signed in). Choose another model in Settings or enable the plugin and retry."));
        }
        var request = new ChatRequest(config,
        [
            new ChatMessage("system", "You draft Git commit messages from diffs. Match the repository's recent commit style and format. " +
                (HostText.Language == UiLanguage.Zh ? "Write the commit message in Simplified Chinese. " : "Write the commit message in English. ") +
                "Output only the commit message, never explanations or code fences."),
            new ChatMessage("user", prompt),
        ], []);
        var text = new StringBuilder();
        await foreach (var chunk in provider.StreamChatAsync(request, ct))
        {
            if (chunk.Type == ChunkType.TextDelta) text.Append(chunk.TextDelta);
            else if (chunk.Type == ChunkType.Error) throw new InvalidOperationException(L.Pick("生成提交信息失败：", "Failed to generate a commit message: ") + chunk.ErrorMessage);
            else if (chunk.Type is ChunkType.ToolCallDelta or ChunkType.ToolCallComplete) throw new InvalidOperationException(L.Pick("模型意外返回工具调用；请重试。", "The model unexpectedly returned a tool call; retry."));
            if (text.Length > 8000) break;
        }
        return text.ToString();
    }

    private List<CommandInfo> CommandList(Bundle bundle)
    {
        var commands = CommandCatalog.Builtins
            .Select(hint => new CommandInfo(hint.Text, hint.Description, hint.Usage, hint.Advanced, hint.IsSkill, hint.Aliases))
            .ToList();
        commands.AddRange(_plugins.Commands.Select(command => new CommandInfo("/ext " + command.Name, command.Description, L.Pick("参数", "arguments"), Advanced: true)));
        commands.AddRange(bundle.Catalog.List().Where(skill => skill.Enabled && skill.UserInvocable)
            .Select(skill => new CommandInfo("/" + skill.Name, skill.Description, L.Pick("任务参数", "task arguments"), Advanced: true, IsSkill: true)));
        return commands;
    }

    private List<WorkbenchConversation> WorkbenchList(Bundle bundle)
    {
        var workspace = bundle.Workspace;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        return bundle.Store.Conversations
            .Where(conversation => conversation.ParentConversationId == null)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Select(conversation => new WorkbenchConversation(
                conversation.Id, WorkbenchTitle(bundle, conversation), conversation.ModelId, conversation.ProviderId,
                conversation.CreatedAt, conversation.UpdatedAt, workspace, name, conversation.AgentRole, conversation.ParentConversationId))
            .ToList();
    }

    /// <summary>Terminal-created conversations keep a placeholder title; derive one from the first prompt.</summary>
    private static string WorkbenchTitle(Bundle bundle, Conversation conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation.Title) && conversation.Title != "New Conversation") return conversation.Title;
        var prompt = bundle.Store.GetVisibleMessages(conversation.Id).FirstOrDefault(message => message.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(prompt)) return L.Pick("新对话", "New conversation");
        var line = prompt.Replace('\n', ' ').Trim();
        return line.Length > 40 ? string.Concat(line.AsSpan(0, 40), "…") : line;
    }

    /// <summary>Active workspace first, then recently opened ones from the project index.</summary>
    private List<WorkspaceEntry> WorkspaceList()
    {
        var info = Info();
        var entries = new List<WorkspaceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(info.Workspace));
        var running = RunningList().GroupBy(run => run.DataDirectory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        void Add(string workspace, string dataDirectory)
        {
            if (!Directory.Exists(workspace)) return;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (!seen.Add(full)) return;
            var name = Path.GetFileName(full);
            var isCurrent = string.Equals(full, current, StringComparison.OrdinalIgnoreCase);
            // The active workspace counts top-level sessions exactly; the manifest count
            // of another workspace includes child sessions.
            var count = isCurrent
                ? _current.Store.Conversations.Count(conversation => conversation.ParentConversationId == null)
                : CountSessions(dataDirectory);
            entries.Add(new WorkspaceEntry(full, string.IsNullOrEmpty(name) ? full : name, dataDirectory, count, isCurrent, running.GetValueOrDefault(dataDirectory)));
        }
        Add(info.Workspace, info.DataDirectory);
        foreach (var entry in ProjectDataDirectory.List().OrderByDescending(Stamp).Take(12)) Add(entry.Workspace, entry.DataDirectory);
        return entries;
    }

    private static int CountSessions(string dataDirectory)
    {
        try
        {
            var manifest = Path.Combine(dataDirectory, "allagent.json");
            if (File.Exists(manifest))
            {
                var probe = JsonSerializer.Deserialize<ManifestProbe>(File.ReadAllText(manifest), ProbeOptions);
                if (probe?.ConversationFiles is { } files) return files.Count;
            }
            var directory = Path.Combine(dataDirectory, "conversations");
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).Select(name => name.Length > 32 ? name[..32] : name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return 0; }
    }

    private sealed record ManifestProbe(List<FileRef>? ConversationFiles);
    private sealed record FileRef(Guid Id, string Revision);
    private static readonly JsonSerializerOptions ProbeOptions = new() { PropertyNameCaseInsensitive = true };

    private static void ImportSkillText(SkillCatalog catalog, string content, string scope)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException(L.Pick("请提供 SKILL.md 内容（YAML frontmatter + 指令）。", "Provide SKILL.md content (YAML frontmatter + instructions)."));
        if (content.Length > 1_000_000) throw new ArgumentException(L.Pick("SKILL.md 超过 1 MB。", "SKILL.md exceeds 1 MB."));
        var staging = Path.Combine(Path.GetTempPath(), "allagent-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "SKILL.md"), content);
            catalog.Import(staging, string.IsNullOrWhiteSpace(scope) ? "user" : scope);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* staging cleanup is best effort */ }
        }
    }

    private async Task<WorkspaceInfo> SwitchWorkspaceAsync(string path, bool create)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(L.Pick("请提供工作区目录路径。", "Provide a workspace directory path."));
        var full = Path.GetFullPath(path.Trim());
        if (string.Equals(full, _current.Workspace, StringComparison.OrdinalIgnoreCase)) return Info();
        if (!Directory.Exists(full))
        {
            if (!create) throw new InvalidOperationException(L.Pick("目录不存在：", "Directory does not exist: ") + full + L.Pick("。可以改用「新建并切换」来创建它。", ". Use \"Create and switch\" to create it."));
            Directory.CreateDirectory(full);
        }
        // Switching never stops a run: the previous workspace keeps its agents, jobs and journals.
        SetCurrent(CreateBundle(full));
        _pusher.Push(JsonSerializer.Serialize(new { type = "workspace.changed", workspace = _current.Workspace, dataDirectory = _current.DataDirectory }, JsonOptions.Default));
        await EvictIdleAsync();
        return Info();
    }

    private static string WorkspacesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AllAgent.NET");

    private int _picking;

    private WorkspaceInfo Info() => new(_current.Workspace, _current.DataDirectory, _sharedDirectory, WorkspacesRoot);

    private List<WorkspaceInfo> Recent()
        => ProjectDataDirectory.List()
            .Where(entry => Directory.Exists(entry.Workspace))
            .OrderByDescending(Stamp)
            .Take(12)
            .Select(entry => new WorkspaceInfo(entry.Workspace, entry.DataDirectory, _sharedDirectory, WorkspacesRoot))
            .ToList();

    private static DateTime Stamp(ProjectDataDirectory.ProjectEntry entry)
    {
        try
        {
            var marker = Path.Combine(entry.DataDirectory, "allagent.json");
            if (File.Exists(marker)) return File.GetLastWriteTimeUtc(marker);
            return Directory.GetLastWriteTimeUtc(entry.Workspace);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Shows a file or directory the browser cannot navigate to. Relative references resolve
    /// against the workspace the browser is currently on; a path that is not there is an error.</summary>
    private bool OpenPath(string path)
    {
        var raw = (path ?? string.Empty).Trim().Trim('"');
        if (raw.Length == 0) throw new ArgumentException(L.Pick("路径为空。", "The path is empty."));
        var full = Path.GetFullPath(raw, _current.Workspace);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException(L.Pick($"找不到路径：{full}", $"Path not found: {full}"));
        return PathOpener.Open(full);
    }

    private PluginsSnapshot PluginSnapshot() => new(
        _plugins.Status,
        _plugins.Providers.Select(provider => new PluginProviderInfo(provider.ProviderId)).ToArray(),
        _plugins.Authentication.Select(auth => new PluginAuthInfo(auth.Id, auth.Name)).ToArray(),
        _plugins.HookStatus.Select(hook => new HookInfo(hook.Name, hook.Enabled)).ToArray());

    /// <summary>Re-installs the plugin bridge slot and tells connected pages to reload contributions.</summary>
    private void SyncPluginWeb()
    {
        _dispatcher.Install(PluginWebSlot, _plugins.WebBridgeHandlers);
        _pusher.Push(JsonSerializer.Serialize(new { type = "web.contributions.changed" }, JsonOptions.Default));
    }

    /// <summary>Plugin backend → browser frames: rebroadcast as plugin.&lt;packageId&gt;.&lt;event&gt;.</summary>
    private void OnPluginWebPush(string packageId, string eventName, object? data)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                eventName = "plugin." + packageId.ToLowerInvariant() + "." + eventName,
                timestamp = DateTime.UtcNow.ToString("o"),
                data = data ?? new { },
            }, JsonOptions.Default);
            _pusher.Push(payload);
        }
        catch (Exception ex) { Console.Error.WriteLine("[WebUi] plugin web push failed: " + ex.Message); }
    }

    private SessionControlState ControlState()
    {
        var running = _current.Agents.HasActiveRuns || _current.Tasks.Active || RunningList().Count > 0;
        if (_controls == null) return new SessionControlState("restricted", L.Pick("限制模式", "Restricted"), "auto", L.Pick("首次任务时验证", "Verified on the first task"), running);
        return _controls.Snapshot() with { Running = running };
    }

    /// <summary>Broadcasts a host-side permission change (plan approval, terminal command) so every
    /// connected page updates its picker without waiting for a reload.</summary>
    private void PushControls() => _pusher.Push(JsonSerializer.Serialize(new { type = "session.controls", data = ControlState() }, JsonOptions.Default));

    private async Task<InstalledPlugin> Adopt(InstalledPlugin installed, CancellationToken ct)
    {
        if (_plugins.Installed.Any(item => item.PackageId.Equals(installed.PackageId, StringComparison.OrdinalIgnoreCase)))
            await _plugins.SetEnabledAsync(installed.PackageId, false);
        _plugins.Save(installed);
        await _plugins.LoadEnabledAsync(ct);
        return installed;
    }

    private static string WriteImportFile(PluginImportParams parameters)
    {
        if (parameters.Base64.Length > 90_000_000) throw new InvalidDataException(L.Pick("插件包超过 64MB。", "The plugin package exceeds 64 MB."));
        var name = Path.GetFileName(parameters.FileName);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.Pick("请选择 .nupkg 插件包。", "Choose a .nupkg plugin package."));
        var path = Path.Combine(Path.GetTempPath(), "allagent-plugin-" + Guid.NewGuid().ToString("N") + ".nupkg");
        File.WriteAllBytes(path, Convert.FromBase64String(parameters.Base64));
        return path;
    }
}
