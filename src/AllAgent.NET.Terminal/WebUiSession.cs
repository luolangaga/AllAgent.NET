using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

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
public sealed record WorkspaceEntry(string Workspace, string Name, string DataDirectory, int SessionCount, bool IsCurrent);
public sealed record WorkbenchConversation(Guid Id, string Title, string ModelId, string ProviderId, DateTime CreatedAt, DateTime UpdatedAt, string Workspace, string WorkspaceName, string? AgentRole, Guid? ParentId);

// Instruction-skill catalog (SKILL.md), the system the terminal's /skills manages.
public sealed record SkillInfo(string Name, string Description, string Scope, bool Enabled, bool UserInvocable, bool ModelInvocable);
public sealed record SkillCatalogSnapshot(IReadOnlyList<SkillInfo> Skills, IReadOnlyList<string> Diagnostics);
public sealed record SkillNameParams(string Name);
public sealed record SkillToggleParams(string Name, bool Enabled);
public sealed record SkillCreateParams(string Name, string Description, string Instructions, string Scope);
public sealed record SkillImportParams(string Content, string Scope);
public sealed record SkillInvokeParams(string Name, string Arguments);
public sealed record SkillInvokeResult(string Text, IReadOnlyList<ImageAttachment> Images);
public sealed record WorkspaceCreateParams(string Name);
public sealed record WorkspaceFilesParams(string Query = "", int Limit = 40);

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
    private sealed record Bundle(
        DataStore Store, McpToolService Mcp, BackgroundTasks Tasks, WorkspaceCheckpoints Checkpoints, SkillCatalog Catalog,
        AgentService Agents, string Workspace, string DataDirectory, bool Owned);

    private readonly BridgeDispatcher _dispatcher = new();
    private readonly BridgeEventPusher _pusher = new();
    private readonly WebApprovals _approvals;
    private readonly AiProviderFactory _factory;
    private readonly PluginHost _plugins;
    private readonly WebUiTerminalHost _terminal;
    private readonly Func<AgentOptions> _baseOptions;
    private readonly string _sharedDirectory;
    private readonly int _backgroundConcurrency;
    private readonly Func<ApiConfig, IAiProvider>? _providerResolver;
    private readonly SessionControls? _controls;
    private WebUiServer _server = null!;
    private Bundle _bundle = null!;

    public string Url => _server.Url;
    public int Port => _server.Port;
    public int ConnectedClients => _server.ConnectedClients;

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
        _approvals = new WebApprovals(_dispatcher, _pusher)
        {
            PlanMode = () => controls?.PlanMode() ?? false,
            FullAccess = () => controls?.FullAccess() ?? false,
        };
    }

    public static WebUiSession Start(WebUiTerminalHost terminal, Func<AgentOptions> baseOptions, AiProviderFactory factory, PluginHost plugins,
        string sharedDirectory, int backgroundConcurrency, Func<ApiConfig, IAiProvider>? providerResolver = null, SessionControls? controls = null, int port = 0)
    {
        var session = new WebUiSession(terminal, baseOptions, factory, plugins, sharedDirectory, backgroundConcurrency, providerResolver, controls);
        session.RegisterStaticHandlers();
        session._bundle = session.CreateBundle(terminal.Workspace);
        session.RegisterBundleHandlers();
        session._approvals.Checkpoints = session._bundle.Checkpoints;
        session._server = WebUiServer.Start(session._dispatcher, session._pusher, DefaultWebRoot, port);
        session._approvals.HasClients = () => session._server.ConnectedClients > 0;
        // A reloaded page starts with an empty prompt list, so hand it the prompts still waiting.
        session._server.ClientConnected = session._approvals.ReplayPending;
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

    public string StatusText() => $"工作区：{_bundle.Workspace}\n数据目录：{_bundle.DataDirectory}\n地址：{_server.Url}\n浏览器连接：{_server.ConnectedClients} 个\n停止：/web-ui stop";

    /// <summary>Rebuilds the browser runtime so a terminal-side change (plan mode, sandbox, …) takes effect immediately.</summary>
    public async Task RefreshRuntimeAsync()
    {
        if (_bundle.Agents.HasActiveRuns || _bundle.Tasks.Active) return;
        var previous = _bundle;
        _bundle = CreateBundle(previous.Workspace);
        RegisterBundleHandlers();
        _approvals.Checkpoints = _bundle.Checkpoints;
        await DisposeBundleAsync(previous);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await DisposeBundleAsync(_bundle);
    }

    // ── Bundles ───────────────────────────────────────────────────────────────

    private Bundle CreateBundle(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        if (!Directory.Exists(full)) throw new InvalidOperationException("工作区目录不存在：" + full);
        if (string.Equals(full, _terminal.Workspace, StringComparison.OrdinalIgnoreCase))
        {
            // Web connections belong to the explicit Web UI lifetime. Do not
            // start services on page open or retain them in the terminal on stop.
            var webMcp = new McpToolService(_terminal.Store, new SkillExecutor()) { AuthenticationResolver = _terminal.Mcp.AuthenticationResolver };
            return new Bundle(
                _terminal.Store, webMcp, _terminal.Tasks, _terminal.Checkpoints, _terminal.Catalog,
                BuildAgents(_terminal.Store, webMcp, _terminal.Tasks, _terminal.Checkpoints, _terminal.Catalog, full, owned: false),
                full, _terminal.DataDirectory, Owned: false);
        }
        var dataDirectory = LegacyDataPaths.ProjectDirectory(full);
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
        return new Bundle(
            store, mcp, tasks, checkpoints, catalog,
            BuildAgents(store, mcp, tasks, checkpoints, catalog, full, owned: true),
            full, dataDirectory, Owned: true);
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
            Approver = _approvals.ApproveAsync,
            QuestionHandler = _approvals.AskAsync,
            InstructionsProvider = () => TerminalApp.Instructions(workspace, catalog, _approvals.PlanMode?.Invoke() == true),
        };
        var agents = new AgentService(store, _factory, options, mcp);
        agents.ProviderResolver = _providerResolver;
        agents.RuntimeInitializer = runtime =>
        {
            runtime.Tools.Register(new McpResourceTools(mcp));
            // Plugin services resolve the terminal's store, so plugin tools only
            // run while the browser workspace matches the terminal's own.
            if (!owned) runtime.Tools.Register(_plugins.ToolSet());
        };
        return agents;
    }

    private static async Task DisposeBundleAsync(Bundle bundle)
    {
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
            if (name.Length == 0) throw new ArgumentException("请输入工作区名称。");
            if (name.Length > 64) throw new ArgumentException("工作区名称最长 64 个字符。");
            if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("名称不能包含 \\ / : * ? \" < > | 等字符。");
            var directory = Path.Combine(WorkspacesRoot, name);
            Directory.CreateDirectory(directory);
            return SwitchWorkspaceAsync(directory, false);
        });
        _dispatcher.Register<WorkspaceInfo>("workspace.get", _ => Task.FromResult(Info()));
        _dispatcher.Register<List<WorkspaceInfo>>("workspace.recent", _ => Task.FromResult(Recent()));
        // `@`-reference completion in the browser composer: workspace-relative paths only.
        _dispatcher.Register<WorkspaceFilesParams, IReadOnlyList<WorkspaceFileEntry>>("workspace.files", (parameters, _) =>
            Task.FromResult(WorkspaceFiles.Match(_bundle.Workspace, parameters.Query, parameters.Limit)));
        _dispatcher.Register<PluginsSnapshot>("plugins.list", _ => Task.FromResult(PluginSnapshot()));
        _dispatcher.Register<PluginToggleParams, bool>("plugins.setEnabled", async (parameters, _) => { await _plugins.SetEnabledAsync(parameters.PackageId, parameters.Enabled); return true; });
        _dispatcher.Register<PluginRemoveParams, bool>("plugins.remove", async (parameters, _) => { await _plugins.RemoveAsync(parameters.PackageId); return true; });
        _dispatcher.Register<PluginSearchParams, PluginSearchResult>("plugins.search", (parameters, ct) => installer.SearchAsync(parameters.Query, parameters.Skip, parameters.Take, ct));
        _dispatcher.Register<PluginInstallParams, InstalledPlugin>("plugins.install", async (parameters, ct) => await Adopt(await installer.InstallAsync(parameters.Id, parameters.Version, ct), ct));
        _dispatcher.Register<PluginRunCommandParams, string>("plugins.runCommand", async (parameters, ct) =>
        {
            var command = _plugins.Commands.SingleOrDefault(c => c.Name == parameters.Name) ?? throw new ArgumentException("插件命令不存在；请先在插件页启用对应插件。");
            return ToolResult.Clip(await command.Execute(parameters.Args.ToArray(), ct), 16000);
        });
        _dispatcher.Register<PluginImportParams, InstalledPlugin>("plugins.import", async (parameters, ct) => await Adopt(await installer.ImportAsync(WriteImportFile(parameters), ct), ct));
        _dispatcher.Register<HookToggleParams, bool>("plugins.setHookEnabled", (parameters, _) => { _plugins.SetHookEnabled(parameters.Name, parameters.Enabled); return Task.FromResult(true); });
        _dispatcher.Register<SessionControlState>("session.controls", _ => Task.FromResult(ControlState()));
        _dispatcher.Register<SessionPermissionParams, SessionControlState>("session.setPermission", async (parameters, _) =>
        {
            if (_controls == null) throw new InvalidOperationException("当前宿主不支持权限切换。");
            await _controls.SetPermission(parameters.Mode);
            return ControlState();
        });
        _dispatcher.Register<SessionSandboxParams, SessionControlState>("session.setSandbox", async (parameters, _) =>
        {
            if (_controls == null) throw new InvalidOperationException("当前宿主不支持沙箱切换。");
            await _controls.SetSandbox(parameters.Mode);
            return ControlState();
        });
    }

    private void RegisterBundleHandlers()
    {
        var registrar = new BridgeHandlerRegistrar(_dispatcher, _bundle.Store, _bundle.Agents, _factory, _pusher, _bundle.Mcp);
        registrar.RegisterAll();
        // The terminal resolves account-backed credentials; prefer it over the plain provider factory.
        if (_providerResolver != null)
        {
            _dispatcher.Register<TestApiConfigParams, ApiConfigDto>("apiConfigs.refreshModels", async (parameters, ct) =>
            {
                var config = _bundle.Store.GetApiConfig(parameters.Id) ?? throw new Exception("ApiConfig not found");
                var models = await _providerResolver(config).ListModelsAsync(config, ct);
                if (models.Count == 0) throw new InvalidOperationException("接口返回了空模型列表，已有列表未修改。");
                config.Models = models.Select(model => model.Id).ToList();
                ModelCatalog.Remember(config, models);
                _bundle.Store.UpdateApiConfig(config);
                return BridgeHandlerRegistrar.ToDto(config);
            });
        }
        _dispatcher.Register<ExportParams, string>("conversations.export", (parameters, _) =>
        {
            var conversation = _bundle.Store.GetConversation(parameters.Id) ?? throw new Exception("Conversation not found");
            return Task.FromResult(SessionTranscript.Format(conversation));
        });
        // Child sessions belong to the Tasks view; the sidebar shows top-level
        // conversations of the active workspace, each tagged with its workspace.
        _dispatcher.Register<List<WorkbenchConversation>>("conversations.list", _ => Task.FromResult(WorkbenchList()));
        _dispatcher.Register<List<WorkspaceEntry>>("workspaces.list", _ => Task.FromResult(WorkspaceList()));
        // Questions/approvals still waiting for an answer: a page that loaded after the
        // prompt was raised pulls these so the blocked run stays answerable.
        _dispatcher.Register<List<object>>("interactions.pending", _ => Task.FromResult(_approvals.PendingPrompts().ToList()));
        // Native folder dialog on the host. Non-blocking: the dialog runs on its own
        // STA thread and the pick arrives as a `workspace.folderPicked` event, so the
        // bridge stays responsive while the user browses folders.
        _dispatcher.Register<bool>("workspace.pickFolder", _ =>
        {
            if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0) throw new InvalidOperationException("文件夹选择窗口已经打开。");
            Task.Run(() =>
            {
                string? path = null;
                string? error = null;
                try { path = NativeFolderPicker.Pick("选择工作区目录"); }
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
        var catalog = _bundle.Catalog;
        _dispatcher.Register<SkillCatalogSnapshot>("skills.list", _ => Task.FromResult(new SkillCatalogSnapshot(
            catalog.List().Select(skill => new SkillInfo(skill.Name, skill.Description, skill.Scope, skill.Enabled, skill.UserInvocable, skill.ModelInvocable)).ToArray(),
            catalog.Diagnostics.ToArray())));
        _dispatcher.Register<SkillNameParams, string>("skills.body", (parameters, _) => Task.FromResult(catalog.Get(parameters.Name).Instructions));
        _dispatcher.Register<SkillToggleParams, bool>("skills.setEnabled", (parameters, _) => { catalog.Enable(parameters.Name, parameters.Enabled); return Task.FromResult(true); });
        _dispatcher.Register<SkillNameParams, bool>("skills.remove", (parameters, _) => { catalog.Remove(parameters.Name); return Task.FromResult(true); });
        _dispatcher.Register<SkillCreateParams, bool>("skills.create", (parameters, _) =>
        {
            catalog.Create(parameters.Name, parameters.Description, parameters.Instructions, string.IsNullOrWhiteSpace(parameters.Scope) ? "user" : parameters.Scope);
            return Task.FromResult(true);
        });
        _dispatcher.Register<SkillImportParams, bool>("skills.importText", (parameters, _) =>
        {
            ImportSkillText(catalog, parameters.Content, parameters.Scope);
            return Task.FromResult(true);
        });
        // User-invocable skills mirror the terminal's default slash branch exactly
        // (PromptReferences.ExpandSkillAsync): references in the arguments expand first,
        // then the skill instructions are injected around them.
        _dispatcher.Register<SkillInvokeParams, SkillInvokeResult>("skills.invoke", async (parameters, ct) =>
        {
            var input = await PromptReferences.ExpandWithImagesAsync(parameters.Arguments ?? "", _bundle.Workspace, ct, allowExplicitExternalImages: true);
            return new SkillInvokeResult(catalog.Invoke(parameters.Name, input.Text), input.Images);
        });

        // Account login: the terminal's /login list, driven from the browser. OAuth opens the
        // system browser on the host and reports back through the `auth.changed` event.
        var auth = new WebAuthService(_bundle.Store, _bundle.DataDirectory, _plugins, _pusher, () => _bundle.Agents.HasActiveRuns || _bundle.Tasks.Active);
        _dispatcher.Register<List<AuthProviderInfo>>("auth.providers", (ct) => auth.ListAsync(ct));
        _dispatcher.Register<AuthLoginParams, bool>("auth.login", (parameters, _) => Task.FromResult(auth.Login(parameters.Id, parameters.NewAccount)));
        _dispatcher.Register<AuthInputParams, AuthProviderInfo>("auth.loginInput", (parameters, ct) => auth.LoginWithInputAsync(parameters.Id, parameters.Secret, ct));
        _dispatcher.Register<AuthLogoutParams, AuthProviderInfo>("auth.logout", (parameters, ct) => auth.LogoutAsync(parameters.Id, ct));
        _dispatcher.Register<AuthStatusParams, AuthProviderInfo>("auth.status", (parameters, ct) => auth.StatusAsync(parameters.Id, ct));

        // Slash command catalog for the web command palette: built-ins plus plugin commands
        // and user-invocable skills — the same sources that feed the terminal's hint panel.
        _dispatcher.Register<List<CommandInfo>>("commands.list", _ => Task.FromResult(CommandList()));

        // Tool catalog with plugin attribution on top of the shared host + MCP listing.
        var pluginOwners = _plugins.PluginTools.ToDictionary(item => item.Tool.Name, item => item.PackageId, StringComparer.Ordinal);
        _dispatcher.Register<ToolsSnapshotDto>("tools.list", async (ct) =>
        {
            var snapshot = await registrar.ListToolsAsync(ct);
            if (pluginOwners.Count == 0) return snapshot;
            return snapshot with { Tools = snapshot.Tools.Select(tool => pluginOwners.TryGetValue(tool.Name, out var packageId) ? tool with { Source = "plugin", Owner = packageId } : tool).ToArray() };
        });
    }

    private List<CommandInfo> CommandList()
    {
        var commands = CommandCatalog.Builtins
            .Select(hint => new CommandInfo(hint.Text, hint.Description, hint.Usage, hint.Advanced, hint.IsSkill, hint.Aliases))
            .ToList();
        commands.AddRange(_plugins.Commands.Select(command => new CommandInfo("/ext " + command.Name, command.Description, "参数", Advanced: true)));
        commands.AddRange(_bundle.Catalog.List().Where(skill => skill.Enabled && skill.UserInvocable)
            .Select(skill => new CommandInfo("/" + skill.Name, skill.Description, "任务参数", Advanced: true, IsSkill: true)));
        return commands;
    }

    private List<WorkbenchConversation> WorkbenchList()
    {
        var workspace = _bundle.Workspace;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        return _bundle.Store.Conversations
            .Where(conversation => conversation.ParentConversationId == null)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Select(conversation => new WorkbenchConversation(
                conversation.Id, WorkbenchTitle(conversation), conversation.ModelId, conversation.ProviderId,
                conversation.CreatedAt, conversation.UpdatedAt, workspace, name, conversation.AgentRole, conversation.ParentConversationId))
            .ToList();
    }

    /// <summary>Terminal-created conversations keep a placeholder title; derive one from the first prompt.</summary>
    private string WorkbenchTitle(Conversation conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation.Title) && conversation.Title != "New Conversation") return conversation.Title;
        var prompt = _bundle.Store.GetVisibleMessages(conversation.Id).FirstOrDefault(message => message.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(prompt)) return "新对话";
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
                ? _bundle.Store.Conversations.Count(conversation => conversation.ParentConversationId == null)
                : CountSessions(dataDirectory);
            entries.Add(new WorkspaceEntry(full, string.IsNullOrEmpty(name) ? full : name, dataDirectory, count, isCurrent));
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
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("请提供 SKILL.md 内容（YAML frontmatter + 指令）。");
        if (content.Length > 1_000_000) throw new ArgumentException("SKILL.md 超过 1 MB。");
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
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请提供工作区目录路径。");
        var full = Path.GetFullPath(path.Trim());
        if (string.Equals(full, _bundle.Workspace, StringComparison.OrdinalIgnoreCase)) return Info();
        if (!Directory.Exists(full))
        {
            if (!create) throw new InvalidOperationException("目录不存在：" + full + "。可以改用「新建并切换」来创建它。");
            Directory.CreateDirectory(full);
        }
        if (_bundle.Agents.HasActiveRuns || _bundle.Tasks.Active) throw new InvalidOperationException("请先完成或取消正在运行的浏览器任务，再切换工作区。");
        var next = CreateBundle(full);
        var previous = _bundle;
        _bundle = next;
        RegisterBundleHandlers();
        _approvals.Checkpoints = next.Checkpoints;
        _pusher.Push(JsonSerializer.Serialize(new { type = "workspace.changed", workspace = next.Workspace, dataDirectory = next.DataDirectory }, JsonOptions.Default));
        await DisposeBundleAsync(previous);
        return Info();
    }

    private static string WorkspacesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AllAgent.NET");

    private int _picking;

    private WorkspaceInfo Info() => new(_bundle.Workspace, _bundle.DataDirectory, _sharedDirectory, WorkspacesRoot);

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

    private PluginsSnapshot PluginSnapshot() => new(
        _plugins.Status,
        _plugins.Providers.Select(provider => new PluginProviderInfo(provider.ProviderId)).ToArray(),
        _plugins.Authentication.Select(auth => new PluginAuthInfo(auth.Id, auth.Name)).ToArray(),
        _plugins.HookStatus.Select(hook => new HookInfo(hook.Name, hook.Enabled)).ToArray());

    private SessionControlState ControlState()
    {
        var running = _bundle.Agents.HasActiveRuns || _bundle.Tasks.Active;
        if (_controls == null) return new SessionControlState("full", "完全访问", "auto", "首次任务时验证", running);
        return _controls.Snapshot() with { Running = running };
    }

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
        if (parameters.Base64.Length > 90_000_000) throw new InvalidDataException("插件包超过 64MB。");
        var name = Path.GetFileName(parameters.FileName);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 .nupkg 插件包。");
        var path = Path.Combine(Path.GetTempPath(), "allagent-plugin-" + Guid.NewGuid().ToString("N") + ".nupkg");
        File.WriteAllBytes(path, Convert.FromBase64String(parameters.Base64));
        return path;
    }
}
