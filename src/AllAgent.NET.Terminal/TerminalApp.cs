using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Localization;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Core.Extensions;
using System.Runtime.CompilerServices;
using System.Text;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class ChatExit(string message) : Exception(message);

internal static class TerminalApp
{
    internal const int MaxProjectInstructionCharacters = 100000;
    public static async Task<int> RunAsync(string[] raw)
    {
        ApiConfig? inheritedConfig = null;
        var planMode = false;
        var simpleMode = false;
        var restartWebUi = false;
        var restartWebUiPort = 0;
        string[]? recovery = null;
        try
        {
            while (true)
            {
                try
                {
                    var result = await RunWorkspaceAsync(raw, inheritedConfig, planMode, simpleMode, restartWebUi, restartWebUiPort);
                    if (result != 0 && recovery != null)
                    {
                        Console.Error.WriteLine(L.Pick("工作区初始化失败，正在恢复原会话。", "Workspace initialization failed; restoring the previous conversation."));
                        raw = recovery; recovery = null;
                        continue;
                    }
                    return result;
                }
                catch (WorkspaceChange change)
                {
                    raw = change.Arguments; recovery = change.PreviousArguments;
                    inheritedConfig = change.Config; planMode = change.PlanMode; simpleMode = change.SimpleMode;
                    restartWebUi = change.WebUiWasRunning; restartWebUiPort = change.WebUiPort;
                }
            }
        }
        finally
        {
            // Every workspace host is process-wide; the exit path is the only place that releases them.
            await TerminalHosts.DisposeAllAsync();
        }
    }

    private static async Task<int> RunWorkspaceAsync(string[] raw, ApiConfig? inheritedConfig, bool planMode, bool simpleMode, bool restartWebUi = false, int restartWebUiPort = 0)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8; Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        TerminalUi? ui = null;
        try
        {
            var args = TerminalArguments.Parse(raw); ui = new(args.Flags.Contains("json"), args.Flags.Contains("verbose"));
            HostText.Set(HostText.Resolve(LanguageProbe.Read(args.SettingsDirectory)));
            if (args.Flags.Contains("help") || args.Command == "help") { Help(); return 0; }
            var approval = new ToolsApproval(ui) { Permission = args.Permission, PlanMode = planMode, SimpleMode = simpleMode };
            Func<PlanProposal, CancellationToken, Task<PlanDecision>> planHandler = (_, _) =>
                Task.FromResult(new PlanDecision(false, L.Pick("计划审批尚未初始化。", "Plan approval is not initialized yet.")));
            // The permission stance lives on the mutable `approval` object, so every runtime — including
            // ones built before a switch and browser runs in flight — resolves the current mode per call.
            var options = args.Options(approval) with
            {
                PermissionProvider = () => approval.PlanMode ? PermissionMode.Ask : approval.Permission,
                SimpleModeProvider = () => approval.SimpleMode,
                ReadOnlyProvider = () => approval.PlanMode,
                PlanHandler = (plan, ct) => planHandler(plan, ct),
            };
            // PLAN mode keeps every call behind the approver, which denies anything but reads.
            // A plan approval is human-paced, so it must not inherit the short Ask-mode code budget.
            if (planMode) options = options with { Permission = PermissionMode.Ask, CodeTimeout = args.Get("code-timeout") == null ? TimeSpan.FromSeconds(3600) : options.CodeTimeout };
            if (simpleMode) options = options with { SimpleMode = true };
            ui.Workspace = options.WorkspaceRoot;
            if (args.WorkspaceFallback) TerminalOutput.Write(new Text(L.Pick("当前目录不可写（安装目录只读），已改用 ", "The current directory is not writable (the install directory is read-only); using ") + options.WorkspaceRoot + L.Pick(" 作为工作区；用 /workspace <路径> 切换。\n", " as the workspace; use /workspace <path> to switch.\n")));
            // Configuration is global (shared directory); conversations stay per workspace.
            // An explicit --data-dir keeps the old single-manifest layout.
            var sharedConfig = args.Get("data-dir") == null ? args.SharedDirectory : null;
            if (args.Command == "doctor")
            {
                using var doctorStore = new DataStore(args.DataDirectory, false, sharedConfig);
                LanguageSetting.Apply(doctorStore);
                options = options with { SandboxMode = SavedSandboxMode(doctorStore) };
                options.BrowserUse.Root = Path.Combine(args.SharedDirectory, "browser-use");
                options.BrowserUse.Load(doctorStore);
                return await DoctorAsync(args, options, ui);
            }
            if (args.Command == "powershell")
            {
                await PowerShellStoreCommand.ExecuteAsync(new PowerShellModuleStore(args.SharedDirectory, options.PowerShellPath), ui, args.Positionals);
                return 0;
            }
            var dataDir = args.DataDirectory;
            if (args.Command == "tasks")
            {
                if (args.Positionals.FirstOrDefault() is not (null or "list" or "show")) throw new ArgumentException(L.Pick("独立进程仅支持 tasks list/show；授权、回答和取消请在运行任务的聊天界面使用 /tasks。", "A standalone process supports only tasks list/show; approve, answer and cancel from the chat UI that runs the task via /tasks."));
                await using var snapshot = new BackgroundTasks(dataDir, readOnly: true); await new TaskManager(snapshot, ui).ExecuteAsync(args.Positionals.Count == 0 ? ["list"] : args.Positionals); return 0;
            }

            DataDirectoryLease.EnsureNoLegacyWriter(dataDir);
            // The workspace host is process-wide: switching workspaces later reuses it, so runs, jobs,
            // sandboxes and MCP connections of this workspace survive the switch.
            var host = await TerminalHosts.GetOrCreateAsync(args, dataDir, sharedConfig, options, ui);
            await TerminalHosts.EvictIdleAsync(dataDir, sharedConfig, options, ui);
            var store = host.Store;
            LanguageSetting.Apply(store);
            options = options with { SandboxMode = SavedSandboxMode(store) };
            options = options with { SharedDirectory = args.SharedDirectory };
            var browser = host.Browser;
            var computer = host.Computer;
            if (args.Command == "browser") { await browser.ExecuteAsync(args.Positionals); return 0; }
            if (args.Command == "computer") { computer.Execute(args.Positionals); return 0; }
            ValueTask<bool> NotifyInstallationAsync(string message, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                ManagementUi.Output(ui, new { notice = message }, () => TerminalOutput.Write(new Text(message + "\n")));
                return ValueTask.FromResult(true);
            }
            async Task<AgentOptions> CancelableInitializationAsync()
            {
                var configured = options;
                await CancelableAsync(ct => ManagementUi.Busy(L.Pick("初始化运行环境与 PowerShell 能力", "Initializing the runtime and PowerShell support"), async token =>
                {
                    var doctor = new EnvironmentDoctor();
                    var environment = await doctor.EnsureAsync(NotifyInstallationAsync, options.PythonPath, options.PowerShellPath, token);
                    await doctor.EnsureRipgrepAsync(token);
                    await using var browserCheck = new BrowserUseTools(options.BrowserUse);
                    var browserEnvironment = await browserCheck.CheckEnvironmentAsync(token);
                    if (!browserEnvironment.Ok) throw new InvalidOperationException(browserEnvironment.Error);
                    ManagementUi.Success(ui, L.Pick("浏览器环境检查完成：C# / WebView2，不安装 Python 浏览器库或 Chromium。", "Browser environment ready: C# / WebView2; no Python browser libraries or Chromium are installed."));
                    TerminalArguments.DiscoverShellExecutables(args.Shell);
                    configured = options with { PythonPath = environment.Python, PowerShellPath = environment.PowerShell };
                    await using var initializedRuntime = await AgentRuntime.CreateAsync(configured, token);
                    var initializedProbe = await initializedRuntime.ProbeSandboxAsync(token);
                    if (!initializedProbe.Ok) throw new InvalidOperationException(initializedProbe.Error ?? L.Pick("沙箱执行校验失败。", "Sandbox execution verification failed."));
                    ReportSandbox(initializedRuntime, ui);
                    store.SetSetting("environment:initialized", "1");
                }, ct));
                return configured;
            }
            var roles = new AgentRoleCatalog(store);
            LoadingIndicator.Set(store.GetSetting("terminal:spinner"));
            BackgroundTasks? roleTasks = null;
            var roleManager = new AgentRoleManager(roles, ui, name => roleTasks?.List.Any(job =>
                BackgroundTasks.IsActive(job.State) && store.GetConversation(job.SessionId)?.AgentRole == name) == true, () => store.ApiConfigs.ToArray());
            if (args.Command == "agents") { await roleManager.ExecuteAsync(args.Positionals); return 0; }
            ui.Inbox = new InputInbox(store, options.WorkspaceRoot);
            if (args.Command == "stats") { UsageStatistics.Show(store, ui); return 0; }
            var plugins = host.Plugins;
            var tasks = host.Tasks;
            roleTasks = tasks;
            // Main-session runs live beside the background jobs: a session keeps working when the view
            // moves to another conversation, another workspace or a new chat.
            var runs = host.Runs;
            ui.Runs = runs;
            var extensions = new ExtensionManager(plugins, tasks, args.SharedDirectory, ui, args, store);
            var taskManager = new TaskManager(tasks, ui, store);
            var catalog = host.Catalog;
            var checkpoints = host.Checkpoints;
            var checkpointManager = new CheckpointManager(checkpoints, store, ui);
            approval.Checkpoints = checkpoints;
            options = options with { BackgroundTasks = tasks, Checkpoints = checkpoints, QuestionHandler = ui.RequestQuestionAsync, Hooks = plugins.Hooks, Extensions = plugins.Extensions, Skills = catalog };
            var models = new ModelManager(store, ui, args, () => plugins.Providers, extensions.FindAuthentication, () => extensions.Authentication, (id, ct) => extensions.LoginAsync([id], ct));
            var mcp = host.Mcp;
            mcp.AuthenticationResolver = extensions.FindAuthentication;
            var mcpManager = new McpManager(store, mcp, ui, args); var skills = new SkillManager(catalog, ui, args);
            // Declared before command dispatch: the browser workspace host captures these, and CLI mode starts the same host.
            var runtimes = host.Runtimes; var connectedDefaults = false; WebUiSession? webUi = null;
            // GUI/management commands may open the Web host before a terminal view selects a session.
            Conversation conversation = null!;
            plugins.ProvideService(new PluginWorkspaceTasks(
                () => ProjectDataDirectory.List().Where(entry => Directory.Exists(entry.Workspace))
                    .Select(entry => new PluginWorkspace(entry.Workspace, Path.GetFileName(entry.Workspace))).ToArray(),
                (request, ct) =>
                {
                    webUi ??= StartWebUi();
                    webUi.UseTerminalPrompts = args.Command is not ("gui" or "web-ui");
                    return webUi.StartPluginTaskAsync(request, ct);
                })
            { Current = () => options.WorkspaceRoot });
            switch (args.Command)
            {
                case "config": case "providers": await models.ExecuteAsync(args.Positionals); return 0;
                case "models": models.ShowModels(); return 0;
                case "skills": await skills.ExecuteAsync(args.Positionals); return 0;
                case "mcp": await CancelableAsync(ct => mcpManager.ExecuteAsync(args.Positionals, ct)); return 0;
                case "sessions": await SessionsAsync(args, store, ui); return 0;
                case "plugins": await CancelableAsync(ct => extensions.PluginsAsync(args.Positionals, ct)); return 0;
                case "login": await CancelableAsync(ct => extensions.LoginAsync(args.Positionals, ct)); return 0;
                case "logout": await CancelableAsync(ct => extensions.LogoutAsync(args.Positionals, ct)); return 0;
                case "hooks": extensions.Hooks(args.Positionals); return 0;
                case "web-ui":
                {
                    if (!WebUiSession.AssetsAvailable) throw new InvalidOperationException(L.Pick("Web UI 资产缺失：", "Web UI assets are missing: ") + WebUiSession.DefaultWebRoot + L.Pick("。请重新构建后再试。", ". Rebuild and try again."));
                    bool? lan = args.Flags.Contains("no-lan") ? false : args.Flags.Contains("lan") ? true : args.Get("host") is { } listenHost
                        ? listenHost switch
                        {
                            "0.0.0.0" or "*" or "+" or "lan" => true,
                            "127.0.0.1" or "localhost" or "::1" => false,
                            _ => throw new ArgumentException(L.Pick("--host 只支持 0.0.0.0（局域网）或 127.0.0.1（仅本机）。", "--host accepts 0.0.0.0 (LAN) or 127.0.0.1 (this machine only).")),
                        }
                        : null;
                    int? port = null;
                    if (args.Get("port") is { } portText)
                    {
                        if (!int.TryParse(portText, out var parsed) || parsed is < 1 or > 65535) throw new ArgumentException(L.Pick("--port 必须是 1–65535 之间的整数。", "--port must be an integer between 1 and 65535."));
                        port = parsed;
                    }
                    webUi = StartWebUi(network: new WebUiNetworkOptions(lan, port, args.Get("password"), args.Flags.Contains("clear-password")));
                    await using var session = webUi;
                    var info = session.NetworkInfo();
                    WebUiSession.OpenBrowser(info.LocalUrl);
                    ShowWebUi(session, info, true, session.Lan, L.Pick("Web 工作台已启动", "Web workspace started"));
                    await CancelableAsync(async ct =>
                    {
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                        catch (OperationCanceledException) { }
                    });
                    webUi = null;
                    return 0;
                }
                case "gui":
                {
                    try
                    {
                        if (!WebUiSession.AssetsAvailable) throw new InvalidOperationException(L.Pick("Web UI 资产缺失：", "Web UI assets are missing: ") + WebUiSession.DefaultWebRoot + L.Pick("。请重新构建后再试。", ". Rebuild and try again."));
                        if (!GuiShell.Available) throw new InvalidOperationException(L.Pick("GUI 组件缺失：", "GUI components are missing: ") + GuiShell.Path + L.Pick("。请重新构建后再试。", ". Rebuild and try again."));
                        if (args.GuiMode && !TerminalSession.GuiHost)
                        {
                            // Silent launch: a hidden copy of this host owns the window session, so the
                            // terminal returns to the prompt immediately and the window outlives it.
                            using var hidden = System.Diagnostics.Process.Start(TerminalSession.StartInfo(raw, "gui"))
                                ?? throw new InvalidOperationException(L.Pick("无法启动 GUI 后台进程。", "Could not start the background GUI process."));
                            return 0;
                        }
                        if (!GuiShell.TryAcquireInstance(dataDir)) throw new InvalidOperationException(L.Pick("已有一个 AllAgent.NET 窗口在运行。", "An AllAgent.NET window is already running."));
                        webUi = StartWebUi();
                        await using var window = webUi;
                        if (!TerminalSession.GuiHost) TerminalOutput.Write(new Panel(new Text(
                            L.Pick($"GUI 工作台已启动：{webUi.Url}\n工作区：{options.WorkspaceRoot}\n关闭窗口即退出；终端 Ctrl+C 亦可结束。", $"GUI workspace started: {webUi.Url}\nWorkspace: {options.WorkspaceRoot}\nClose the window to exit; Ctrl+C in the terminal also ends it."))).Header("Web UI · GUI"));
                        var exit = await GuiShell.RunAsync(
                            GuiShell.Url(args.Get("gui-url"), webUi.Port), GuiShell.Title(options.WorkspaceRoot),
                            GuiShell.CacheDirectory(args.SharedDirectory), args.Flags.Contains("gui-devtools"));
                        webUi = null;
                        return exit;
                    }
                    catch (Exception ex)
                    {
                        // The detached host has no console: a fatal error must surface as a dialog.
                        if (!TerminalSession.GuiHost) throw;
                        GuiShell.ShowFatal(L.Pick("AllAgent.NET 无法启动 GUI：", "AllAgent.NET could not start the GUI: ") + ex.Message);
                        return 1;
                    }
                }
                case "tasks": await taskManager.ExecuteAsync(args.Positionals); return 0;
                case "tools":
                {
                    await using var listedRuntime = AgentRuntime.CreateCatalog(options);
                    ShowTools(listedRuntime.Tools.Definitions
                        .Concat(new AgentDelegationTools(tasks, (_, _) => throw new InvalidOperationException("Tool listing does not execute agents.")).Definitions)
                        .Concat(plugins.ToolSet().Definitions).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(), ui, listedRuntime.CodeAct.Surface); return 0;
                }
                case "run": case "chat": break;
                default: throw new ArgumentException(L.Pick("未知命令；使用 --help。", "Unknown command; use --help."));
            }
            if (args.Command == "chat" && !ui.Interactive) throw new ArgumentException(L.Pick("交互聊天需要终端；自动化使用 run --json。", "Interactive chat needs a terminal; use run --json for automation."));
            if (args.Command == "chat" && ui.Json) throw new ArgumentException(L.Pick("JSON 事件模式请使用 run --json；chat 使用交互界面。", "Use run --json for JSON events; chat uses the interactive UI."));
            if (args.Command == "run" && args.Positionals.Count == 0) throw new ArgumentException(L.Pick("请提供任务，例如 allagent run \"列出当前目录\"。", "Provide a task, for example allagent run \"list the current directory\"."));
            ui.Verbose = ui.Verbose || store.GetSetting("terminal:verbose") == "on";
            ui.ShowThinking = store.GetSetting("terminal:thinking") != "off";
            conversation = SessionOperations.Get(store, args.Get("resume"), options.WorkspaceRoot);
            ApiConfig? config = inheritedConfig == null ? null : ManagementUi.Clone(inheritedConfig);
            if (config == null && args.Get("config") == null && args.Get("model") == null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALLAGENT_MODEL")))
            {
                var saved = store.ApiConfigs.FirstOrDefault(c => c.IsEnabled && c.Id.ToString() == store.GetSetting("terminal:lastConfig"));
                if (saved != null) config = await models.SelectAsync(saved.Id.ToString());
            }
            var navigationModels = new Dictionary<Guid, ApiConfig>();
            var navigationDrafts = new Dictionary<Guid, string>();
            string RoleOf(Conversation session) => session.ParentConversationId == null ? "primary" : session.AgentRole ?? "general";
            AgentRuntime? CurrentRuntime() => conversation is { } selected ? host.RuntimeFactory.Find(selected.Id) : null;
            async Task DisposeRuntimesAsync()
            {
                // The permission stance is process-wide, so every workspace host rebuilds its sandboxes.
                foreach (var workspace in TerminalHosts.All)
                {
                    await workspace.RuntimeFactory.ClearAsync();
                    workspace.Runtimes.Clear();
                }
            }
            options = options with
            {
                InstructionsProvider = () => Instructions(options.WorkspaceRoot, catalog, approval.PlanMode, includeRoot: true) +
                    (approval.PlanMode || approval.SimpleMode ? "" : "\nAvailable child agents:\n" + string.Join("\n", roles.List().Select(role => role.Name + ": " + role.Description))),
                // Preferences change rarely but must not rewrite the stable instruction prefix that prompt caches reuse.
                VolatileInstructionsProvider = () => QuestionPreference.PreferenceText(QuestionPreference.Current(store))
            };
            IAiProvider Provider(ApiConfig selected)
            {
                var provider = new AiProviderFactory(plugins.Providers).GetProvider(selected);
                return selected.AuthenticationProviderId is { Length: > 0 } id ? new AuthenticatedAiProvider(provider, extensions.FindAuthentication(id)) : provider;
            }
            async Task<SubAgentLaunch> StartDelegatedAsync(SubAgentRequest request, CancellationToken ct)
            {
                var expanded = await PromptReferences.ExpandWithImagesAsync(request.Prompt, options.WorkspaceRoot, ct);
                ct.ThrowIfCancellationRequested();
                return StartJob(request.Prompt, model: request.Model, provider: request.Provider, request: request, expanded: expanded,
                    background: request.Background, ownerToken: request.Background ? default : ct);
            }
            SubAgentLaunch StartJob(string prompt, string kind = "子 Agent", string? model = null, string? provider = null, SubAgentRequest? request = null, PromptReferences.ExpandedPrompt? expanded = null, bool background = false, CancellationToken ownerToken = default)
            {
                var parentId = AgentExecutionScope.ConversationId ?? conversation.Id;
                var parentSession = store.GetConversation(parentId) ?? throw new InvalidOperationException("Parent conversation no longer exists.");
                if (AgentExecutionScope.IsSubAgent) throw new InvalidOperationException(L.Pick("子 Agent 不能再次派发任务。", "A child agent cannot dispatch another task."));
                request ??= new("general", kind + " · " + ToolResult.Clip(prompt, 40), prompt, model, provider);
                var roleDefinition = roles.Get(request.Agent);
                if (roleDefinition.Execution?.MaxDepth == 0) throw new InvalidOperationException(L.Pick("此角色已禁用子代理执行。", "This role disables child-agent execution."));
                if (!roleDefinition.ReadOnly) checkpoints.MarkExternalAction();
                var roleChanged = request.SessionId == null || store.GetConversation(request.SessionId.Value)?.AgentRole != request.Agent;
                var effectiveModel = model ?? (roleChanged ? roleDefinition.Model : null);
                var effectiveProvider = provider ?? (roleChanged ? roleDefinition.Provider : null);
                var selected = ResolveSubAgentConfig(config ?? throw new InvalidOperationException(L.Pick("请先选择模型。", "Select a model first.")), store.ApiConfigs, effectiveModel, effectiveProvider,
                    args.Get("subagent-model") ?? store.GetSetting("subagents:model"), args.Get("subagent-provider") ?? store.GetSetting("subagents:provider"));
                var child = SubAgentSessions.Resolve(store, tasks, parentId, request, allowRunning: true, roles: roles);
                if (request.SessionId != null && effectiveProvider == null && child.SubAgentConfigId is { } savedConfig)
                    selected = SubAgentModelSelection.Resolve(selected, store.ApiConfigs, effectiveModel ?? child.ModelId, savedConfig.ToString());
                if (request.SessionId != null)
                {
                    var joined = tasks.TryJoin(child.Id, parentId, _ =>
                    {
                        if (child.Revert != null) throw new InvalidOperationException(L.Pick("子会话处于暂存撤回状态，不能追加指令。", "The child conversation has a staged undo and cannot accept more instructions."));
                        store.EnqueueInput(child.Id, request.Prompt, expanded?.Text ?? request.Prompt, expanded?.Images ?? [], "steer", selected, request.Agent);
                    });
                    if (joined != null) return new(joined, Joined: true);
                }
                selected = (roleDefinition.Execution ?? new()).Apply(selected);
                child.SubAgentRoleGrant = roleDefinition;
                child.SubAgentPermission = options.CurrentPermission;
                child.SubAgentHostToolsGrant = options.HostToolsProvider?.Invoke()?.ToArray();
                child.SubAgentConfigId = selected.Id; child.ModelId = selected.DefaultModel; child.ProviderId = selected.ProviderId;
                if (request.SessionId == null) store.AddConversation(child); else store.UpdateConversation(child);
                var planning = approval.PlanMode || parentSession.ParentConversationId != null && roles.Get(parentSession.AgentRole ?? "general").ReadOnly;
                var childPermission = planning ? PermissionMode.Ask : approval.Permission;
                var firstRun = true;
                var jobId = tasks.Start(prompt, child.Id, kind, (id, ct) =>
                {
                    var fromInbox = !firstRun; firstRun = false;
                    return RunJobAsync(id, child.Id, prompt, selected, planning, childPermission, ct, fromInbox, expanded);
                }, parentId, () => store.GetPendingInputs(child.Id).Any(input => input.Delivery == "steer"), ownerToken);
                // Background children outlive the parent turn and report by notification; foreground children are cancelled with the parent turn.
                if (background) tasks.NotifyParentOnCompletion(jobId);
                return new(jobId);
            }
            plugins.ProvideService(new AgentTaskLauncher(task => StartJob(task, "插件任务", background: true).TaskId));
            async IAsyncEnumerable<AgentEvent> RunJobAsync(string jobId, Guid session, string prompt, ApiConfig selected, bool planning, PermissionMode permission, [EnumeratorCancellation] CancellationToken ct, bool fromInbox = false, PromptReferences.ExpandedPrompt? snapshot = null)
            {
                AgentRoleDefinition CurrentRole() => roles.Get(store.GetConversation(session)?.AgentRole ?? "general");
                var roleGrant = store.GetConversation(session)?.SubAgentRoleGrant ?? CurrentRole();
                bool ReadOnly() => planning || roleGrant.ReadOnly || CurrentRole().ReadOnly || options.ReadOnlyProvider?.Invoke() == true;
                var rolePolicy = new SubAgentRolePolicy(ReadOnly, options.WorkspaceRoot, options.ExternalDirectoryPermissions, CurrentRole,
                    (_, _) => ValueTask.FromResult(false), parentTools: options.HostToolsProvider, grant: roleGrant,
                    parentToolGrant: store.GetConversation(session)?.SubAgentHostToolsGrant);
                var settings = CurrentRole().Execution ?? new SubAgentExecutionSettings();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(settings.Apply(options).RunTimeout);
                ct = timeout.Token;
                using var slot = await SubAgentConcurrency.AcquireAsync(store, CurrentRole().Name, settings.MaxConcurrent, ct);
                var childOptions = settings.Apply(options) with
                {
                    IsSubAgent = true,
                    HostToolsProvider = () => SubAgentRolePolicy.IntersectTools(CurrentRole().Tools, roleGrant.Tools),
                    ReadOnlyProvider = ReadOnly,
                    Permission = permission,
                    // Delegated work freezes the stance it was dispatched with; a parent switching to full
                    // access mid-flight must never widen a child, and read-only children stay read-only.
                    PermissionProvider = () => DelegatedPermissions.Restrict(permission, options.CurrentPermission),
                    SimpleModeProvider = null,
                    TaskContractProvider = () => store.GetConversation(session)?.Delegation,
                    InvocationPolicy = (tool, arguments) => rolePolicy.Validate(tool, arguments) ?? options.InvocationPolicy?.Invoke(tool, arguments),
                    InvocationAuthorization = async (tool, arguments, token) => (options.InvocationAuthorization == null ? null : await options.InvocationAuthorization(tool, arguments, token)) ?? await rolePolicy.AuthorizeAsync(tool, arguments, token),
                    ConfigurationProvider = (childId, current) =>
                    {
                        var state = store.GetConversation(childId) ?? throw new InvalidOperationException(L.Pick("子会话已不存在。", "The child conversation no longer exists."));
                        // Environment-configured providers need not be persisted in the store.
                        var configurations = store.ApiConfigs.ToList();
                        if (!configurations.Any(item => item.Id == current.Id)) configurations.Add(current);
                        return (CurrentRole().Execution ?? new()).Apply(SubAgentModelSelection.Resolve(current, configurations, state.ModelId, state.SubAgentConfigId?.ToString()));
                    },
                    Checkpoints = null, InstallationApprover = null,
                    InstructionsProvider = () => ((CurrentRole().Execution ?? new()).IncludeProjectInstructions ? Instructions(options.WorkspaceRoot, catalog, ReadOnly(), includeRoot: true) : "") + "\nYou are a delegated agent. Stay within the assigned task. You cannot spawn agents or ask the user questions. Return missing information, blockers and permission failures to the parent agent in your final response. Follow the selected host permission mode; do not bypass a denial." +
                        "\nCurrent agent role: " + CurrentRole().Name + "\n" + CurrentRole().Instructions + (ReadOnly() ? "\nHost restriction: read-only. Do not create, modify, delete, move or copy files, or execute external operations." : ""),
                    Approver = (request, _) => ValueTask.FromResult(!ReadOnly() && request.Preapproved),
                    QuestionHandler = null,
                    PlanHandler = null
                };
                var childRuntime = await host.RuntimeFactory.GetAsync(session, childOptions, initialized =>
                { initialized.Tools.Register(new McpResourceTools(mcp)); initialized.Tools.Register(new McpToolSet(mcp)); initialized.Tools.Register(plugins.ToolSet()); }, ct);
                var childProvider = new SubAgentAiProvider(new RequestRoutingAiProvider(Provider), settings,
                    current => SubAgentModelSelection.Resolve(current, store.ApiConfigs, settings.FallbackModel, settings.FallbackProvider));
                var engine = new AgentEngine(store, childProvider, childRuntime.CodeAct, childOptions);
                if (fromInbox)
                {
                    await foreach (var evt in engine.RunAsync(session, "Continue pending child input.", selected, ct, fromInbox: true)) yield return evt;
                }
                else
                {
                    var expanded = snapshot ?? await PromptReferences.ExpandWithImagesAsync(prompt, options.WorkspaceRoot, ct);
                    await foreach (var evt in engine.RunAsync(session, expanded.Text, selected, ct, images: expanded.Images, inputText: prompt)) yield return evt;
                }
            }
            async Task<AgentEngine> EngineAsync()
            {
                if (config == null || !config.IsEnabled) config = await models.SelectAsync(args.Get("config"), args.Get("model"));
                ui.SetThinkingEffort(config);
                return await BuildEngineAsync(conversation, config);
            }
            async Task<AgentEngine> BuildEngineAsync(Conversation session, ApiConfig sessionConfig)
            {
                var role = RoleOf(session);
                var resumedRole = role == "primary" ? null : roles.Get(role);
                var roleGrant = session.SubAgentRoleGrant ?? resumedRole;
                var resumedPolicy = resumedRole == null ? null : new SubAgentRolePolicy(() => approval.PlanMode || roles.Get(role).ReadOnly,
                    options.WorkspaceRoot, options.ExternalDirectoryPermissions, () => roles.Get(role), approval.ApproveAsync, parentTools: options.HostToolsProvider,
                    grant: roleGrant, parentToolGrant: session.SubAgentHostToolsGrant);
                var executionOptions = (resumedRole?.Execution?.Apply(options) ?? options) with
                {
                    IsSubAgent = role != "primary",
                    TaskContractProvider = () => session.Delegation,
                    HostToolsProvider = () => resumedRole == null ? options.HostToolsProvider?.Invoke() : SubAgentRolePolicy.IntersectTools(roles.Get(role).Tools, roleGrant?.Tools),
                    ReadOnlyProvider = () => approval.PlanMode || roleGrant?.ReadOnly == true || resumedRole != null && roles.Get(role).ReadOnly,
                    Permission = approval.PlanMode || resumedRole?.ReadOnly == true ? PermissionMode.Ask : options.Permission,
                    // Read-only roles keep the stance they were opened with: a later switch to full access
                    // must never widen them. Every other role follows the live stance.
                    PermissionProvider = () => resumedRole == null ? options.CurrentPermission : DelegatedPermissions.Restrict(session.SubAgentPermission ?? options.CurrentPermission, options.CurrentPermission),
                    InvocationPolicy = resumedPolicy == null ? options.InvocationPolicy : (tool, arguments) => resumedPolicy.Validate(tool, arguments) ?? options.InvocationPolicy?.Invoke(tool, arguments),
                    InvocationAuthorization = resumedPolicy == null ? options.InvocationAuthorization : async (tool, arguments, token) => (options.InvocationAuthorization == null ? null : await options.InvocationAuthorization(tool, arguments, token)) ?? await resumedPolicy.AuthorizeAsync(tool, arguments, token),
                    Approver = resumedRole == null ? options.Approver : (request, token) => approval.PlanMode || roles.Get(role).ReadOnly ? ValueTask.FromResult(false) : options.Approver?.Invoke(request, token) ?? ValueTask.FromResult(request.Preapproved),
                    PlanHandler = resumedRole == null ? options.PlanHandler : null,
                    InstructionsProvider = () => resumedRole == null ? options.InstructionsProvider?.Invoke() ?? "" :
                        (roles.Get(role).Execution?.IncludeProjectInstructions == false ? "" : Instructions(options.WorkspaceRoot, catalog, approval.PlanMode)) + "\n" + roles.Get(role).Instructions
                };
                // The factory refreshes policies each turn while preserving per-conversation workers.
                var runtimeKey = session.Id.ToString();
                var runtime = await host.RuntimeFactory.GetAsync(session.Id, executionOptions, initialized =>
                {
                    initialized.Tools.Register(new McpResourceTools(mcp));
                    if (resumedRole == null) initialized.Tools.Register(new AgentDelegationTools(tasks, StartDelegatedAsync));
                });
                runtimes[runtimeKey] = runtime;
                if (!connectedDefaults)
                {
                    await CancelableAsync(ct => ManagementUi.Busy(L.Pick("连接 MCP 服务", "Connecting MCP servers"), token => mcp.ConnectAllEnabledAsync(token), ct)); connectedDefaults = true;
                    foreach (var c in store.McpServerConfigs.Where(c => c.IsEnabled && !mcp.IsConnected(c.Id))) ui.Error("MCP " + c.Name + L.Pick("：", ": ") + mcp.GetError(c.Id));
                }
                runtime.Tools.Replace<McpToolSet>(new McpToolSet(mcp));
                runtime.Tools.Replace<PluginToolSet>((PluginToolSet)plugins.ToolSet());
                runtime.RefreshToolSurface();
                if (FullScreenTerminal.Current != null && session.Id == conversation.Id) ui.Header(options.WorkspaceRoot, sessionConfig.DefaultModel, session.Id);
                IAiProvider executionProvider = new RequestRoutingAiProvider(Provider);
                if (resumedRole != null)
                {
                    var settings = resumedRole.Execution ?? new SubAgentExecutionSettings();
                    executionProvider = new SubAgentAiProvider(executionProvider, settings, current => SubAgentModelSelection.Resolve(current, store.ApiConfigs, settings.FallbackModel, settings.FallbackProvider));
                }
                return new AgentEngine(store, executionProvider, runtime.CodeAct, executionOptions);
            }
            void Navigate(Conversation target)
            {
                if (config != null) navigationModels[conversation.Id] = ManagementUi.Clone(config);
                navigationDrafts[conversation.Id] = ui.Draft;
                conversation = target;
                ui.Draft = navigationDrafts.GetValueOrDefault(target.Id) ?? "";
                if (navigationModels.TryGetValue(target.Id, out var remembered)) config = ManagementUi.Clone(remembered);
                else if (target.SubAgentConfigId is { } saved && store.ApiConfigs.Any(c => c.Id == saved && c.IsEnabled))
                    config = SubAgentModelSelection.Resolve(config ?? store.ApiConfigs.First(c => c.Id == saved), store.ApiConfigs, target.ModelId, saved.ToString());
                ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? target.ModelId, target.Id);
            }
            WebUiSession StartWebUi(int port = 0, WebUiNetworkOptions? network = null) => WebUiSession.Start(
                new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, options.WorkspaceRoot, dataDir),
                () => options,
                new AiProviderFactory(plugins.Providers),
                plugins,
                args.SharedDirectory,
                args.BackgroundConcurrency,
                Provider,
                new SessionControls(
                    () => approval.PlanMode,
                    () => approval.FullAccess,
                    () => new SessionControlState(approval.PlanMode ? "plan" : approval.SimpleMode ? "simple" : approval.ModeKey, approval.ModeLabel, SavedSandboxMode(store).ToString().ToLowerInvariant(), CurrentRuntime()?.SandboxName ?? L.Pick("首次任务时验证", "Verified on the first task"), false),
                    SetPermissionModeAsync,
                    SetSandboxModeAsync),
                port,
                network);
            async Task SetPermissionModeAsync(string mode)
            {
                if (mode is not ("restricted" or "ask" or "full" or "plan" or "simple")) throw new ArgumentException(L.Pick("权限模式：restricted / ask / full / plan / simple。", "Permission mode: restricted / ask / full / plan / simple."));
                // plan and simple are overlays on the selected mode; leaving them restores that mode.
                if (mode is not ("plan" or "simple")) approval.Permission = mode switch { "full" => PermissionMode.Full, "ask" => PermissionMode.Ask, _ => PermissionMode.Restricted };
                approval.PlanMode = mode == "plan";
                approval.SimpleMode = mode == "simple";
                options = options with { Permission = approval.PlanMode ? PermissionMode.Ask : approval.Permission, SimpleMode = approval.SimpleMode, CodeTimeout = args.Get("code-timeout") == null ? TimeSpan.FromSeconds(approval.PlanMode ? 3600 : approval.Permission == PermissionMode.Ask ? 60 : 600) : options.CodeTimeout };
                // The live providers make the new stance apply to every in-flight call at once. Sandboxes are
                // rebuilt only when idle: disposing a runtime a run is using would abort that run.
                if (!TerminalHosts.All.Any(workspace => workspace.Busy)) await DisposeRuntimesAsync();
                if (webUi != null) await webUi.RefreshRuntimeAsync();
            }
            planHandler = async (plan, ct) =>
            {
                if (!await ui.RequestPlanApprovalAsync(plan, ct))
                    return new PlanDecision(false, "The human rejected the plan. Revise it and call plan_propose again.");
                await SetPermissionModeAsync("full");
                ManagementUi.Success(ui, L.Pick("计划已批准：已切换到完全访问，AI 将继续执行。", "Plan approved: switched to full access; the AI will continue."));
                return new PlanDecision(true, Mode: "full");
            };
            async Task SetSandboxModeAsync(string mode)
            {
                if (mode is not ("auto" or "hyperlight" or "monty")) throw new ArgumentException(L.Pick("沙箱模式：auto / hyperlight / monty。", "Sandbox mode: auto / hyperlight / monty."));
                if (TerminalHosts.All.Any(workspace => workspace.Busy)) throw new InvalidOperationException(L.Pick("请先完成或停止正在运行的会话与后台任务，再切换沙箱。", "Finish or stop running sessions and background tasks before switching the sandbox."));
                await DisposeRuntimesAsync();
                store.SetSetting("terminal:sandbox", mode);
                options = options with { SandboxMode = mode switch { "hyperlight" => SandboxMode.Hyperlight, "monty" => SandboxMode.Monty, _ => SandboxMode.Auto } };
                if (webUi != null) await webUi.RefreshRuntimeAsync();
            }
            string LanguageLabel(string value) => value switch
            {
                "zh" => "中文",
                "en" => "English",
                _ => L.Pick("跟随系统", "Follow system") + " · " + L.Pick("当前 ", "currently ") + (L.Language == UiLanguage.Zh ? "中文" : "English"),
            };
            void ApplyLanguage(string value)
            {
                LanguageSetting.Save(store, value);
                FullScreenTerminal.Current?.Draw();
                webUi?.PushSettingsChanged();
                ManagementUi.Success(ui, L.Pick("界面语言已切换：", "Interface language switched: ") + LanguageLabel(value));
            }
            WebUiNetworkOptions? ParseWebUiNetwork(string[] values)
            {
                bool? lan = null; int? port = null; string? password = null; var clear = false; var changed = false;
                string Next(ref int index)
                {
                    if (++index >= values.Length) throw new ArgumentException(L.Pick("缺少参数值。", "Missing option value."));
                    return values[index];
                }
                for (var index = 0; index < values.Length; index++)
                {
                    var token = values[index];
                    if (token is "qr" or "--qr") continue;
                    var equals = token.IndexOf('=');
                    var name = equals > 0 ? token[..equals] : token;
                    var inline = equals > 0 ? token[(equals + 1)..] : null;
                    switch (name)
                    {
                        case "--lan":
                            lan = true; changed = true; break;
                        case "--local": case "--loopback": case "--no-lan":
                            lan = false; changed = true; break;
                        case "--host":
                            var host = inline ?? Next(ref index);
                            lan = host switch
                            {
                                "0.0.0.0" or "*" or "+" or "lan" => true,
                                "127.0.0.1" or "localhost" or "::1" or "[::1]" => false,
                                _ => throw new ArgumentException(L.Pick("只支持 --host 0.0.0.0（局域网）或 --host 127.0.0.1（仅本机）。", "Use --host 0.0.0.0 for the LAN or --host 127.0.0.1 for this machine only.")),
                            };
                            changed = true; break;
                        case "--port":
                            var text = inline ?? Next(ref index);
                            if (!int.TryParse(text, out var parsed) || parsed is < 1 or > 65535)
                                throw new ArgumentException(L.Pick("端口必须是 1–65535 之间的整数。", "The port must be an integer between 1 and 65535."));
                            port = parsed; changed = true; break;
                        case "--password":
                            password = inline ?? Next(ref index);
                            if (password.Length == 0) throw new ArgumentException(L.Pick("密码不能为空。", "The password cannot be empty."));
                            changed = true; break;
                        case "--clear-password":
                            clear = true; changed = true; break;
                        default:
                            throw new ArgumentException(L.Pick(
                                "用法：/web-ui [--lan|--no-lan|--host 0.0.0.0] [--port 端口] [--password 密码] [qr]；/web-ui status；/web-ui stop",
                                "Usage: /web-ui [--lan|--no-lan|--host 0.0.0.0] [--port port] [--password password] [qr]; /web-ui status; /web-ui stop"));
                    }
                }
                return changed ? new WebUiNetworkOptions(lan, port, password, clear) : null;
            }
            void ShowWebUi(WebUiSession session, WebUiNetworkInfo info, bool opened, bool showQr, string header)
            {
                var text = new StringBuilder();
                text.AppendLine((opened ? L.Pick("已在默认浏览器打开：", "Opened in the default browser: ") : L.Pick("本机访问：", "Local access: ")) + info.LocalUrl);
                if (session.Lan)
                {
                    text.AppendLine(L.Pick("局域网监听：0.0.0.0 · 其他设备地址：", "LAN listener: 0.0.0.0 · addresses for other devices:"));
                    foreach (var url in info.Urls) text.Append("  ").AppendLine(url);
                    text.AppendLine(session.PasswordConfigured
                        ? L.Pick("访问密码：已设置（其他设备可输入密码，或扫描二维码直接进入）。", "Access password: set (other devices can enter it or scan the QR code).")
                        : L.Pick("访问密码：未设置 ⚠ 建议设置：在 Web 设置页中设置，或执行 /web-ui --password 你的密码。其他设备目前只能通过二维码进入。", "Access password: not set ⚠ Set one in the Web settings page or run /web-ui --password your-password. Other devices can currently enter only through the QR code."));
                    text.AppendLine(L.Pick("本机始终免密；请确保其他设备与电脑在同一局域网。首次开启时 Windows 防火墙可能询问是否允许，请选择允许「专用网络」。", "This machine never needs the password; keep other devices on the same LAN. Windows Firewall may ask for permission the first time; allow private networks."));
                }
                else
                {
                    text.AppendLine(L.Pick("监听：仅本机 127.0.0.1 · 本机访问无需密码。", "Listening: loopback 127.0.0.1 · this machine needs no password."));
                    text.AppendLine(L.Pick("需要手机或平板操作时，用 /web-ui --lan 开启局域网访问。", "Use /web-ui --lan when a phone or tablet should connect."));
                }
                text.AppendLine(L.Pick("工作区：", "Workspace: ") + options.WorkspaceRoot);
                text.AppendLine(L.Pick("二维码：/web-ui qr · 状态：/web-ui status · 停止：/web-ui stop", "QR: /web-ui qr · Status: /web-ui status · Stop: /web-ui stop"));
                TerminalOutput.Write(new Panel(new Text(text.ToString())).Header(header));
                if (showQr && session.Lan) TerminalOutput.Write(new Text("\n" + WebUiQr.Text(info.AccessUrl ?? info.Url) + "\n"));
                else if (showQr) TerminalOutput.Write(new Text("\n" + L.Pick("当前仅本机监听，二维码不会被其他设备打开；请先使用 /web-ui --lan。", "The listener is loopback-only, so other devices cannot open the QR code; use /web-ui --lan first.") + "\n"));
            }
            async Task WebUiAsync(string[] rest, bool openBrowser = true, int port = 0)
            {
                var subcommand = rest.FirstOrDefault();
                if (subcommand == "stop")
                {
                    if (webUi == null) { ManagementUi.Success(ui, L.Pick("Web 工作台未在运行。", "The Web workspace is not running.")); return; }
                    await webUi.DisposeAsync();
                    webUi = null;
                    ManagementUi.Success(ui, L.Pick("已停止 Web 工作台，浏览器连接已断开。", "Stopped the Web workspace; browser connections are closed."));
                    return;
                }
                if (subcommand == "status")
                {
                    TerminalOutput.Write(webUi == null
                        ? new Panel(new Text(L.Pick("未运行。使用 /web-ui 启动浏览器工作台，与终端共享工作区、模型与权限。", "Not running. Use /web-ui to start the browser workspace, which shares the terminal's workspace, models and permissions."))).Header(L.Pick("Web 工作台", "Web workspace"))
                        : new Panel(new Text(webUi.StatusText())).Header(L.Pick("Web 工作台正在运行", "Web workspace running")));
                    return;
                }
                if (!WebUiSession.AssetsAvailable) throw new InvalidOperationException("Web UI 资产缺失：" + WebUiSession.DefaultWebRoot + "。请重新构建后再试。");
                var showQr = rest.Any(token => token is "qr" or "--qr");
                var network = ParseWebUiNetwork(rest);
                var changed = false;
                var opened = false;
                WebUiNetworkInfo info;
                if (webUi == null)
                {
                    webUi = StartWebUi(port, network);
                    info = webUi.NetworkInfo();
                    opened = openBrowser && WebUiSession.OpenBrowser(info.LocalUrl);
                }
                else
                {
                    info = network == null
                        ? webUi.NetworkInfo()
                        : await webUi.UpdateNetworkAsync(new WebUiNetworkParams(network.Lan ?? webUi.Lan, network.Port ?? webUi.Port, network.Password, network.ClearPassword));
                    changed = network != null;
                }
                ShowWebUi(webUi, info, opened, showQr || webUi.Lan, changed ? L.Pick("Web 工作台设置已更新", "Web workspace settings updated") : opened ? L.Pick("Web 工作台已启动", "Web workspace started") : L.Pick("Web 工作台正在运行", "Web workspace running"));
            }
            try
            {
                if (args.Command == "run")
                {
                    var engine = await EngineAsync(); if (!ui.Json) ui.Header(options.WorkspaceRoot, config!.DefaultModel, conversation.Id);
                    await RunTurnAsync(runs, engine, conversation.Id, string.Join(" ", args.Positionals), config!, ui, checkpoints, store);
                    return ui.LastReason is "stop" or "STOP" ? 0 : ui.LastReason == "cancelled" ? 130 : 1;
                }
                using var screen = !args.Flags.Contains("inline") && AnsiConsole.Profile.Capabilities.Ansi ? new FullScreenTerminal(store, catalog, mcp, ui) : null;
                using var modelFooter = new ModelFooter(() => extensions.FooterProviders);
                if (screen != null) screen.ModelFooter = () => modelFooter.Read(config, ui.FooterRevision);
                screen?.Enter();
                if (screen != null) screen.ExtensionSummary = () => L.Pick($"插件 · {plugins.Status.Count(p => p.Enabled && p.Error == null)} 个\n任务 · {tasks.List.Count(j => j.State is BackgroundTaskState.Running or BackgroundTaskState.Queued)} 运行 / 排队\n等待处理 · {tasks.List.Count(j => j.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval)} 个  /tasks",
                    $"Plugins · {plugins.Status.Count(p => p.Enabled && p.Error == null)}\nTasks · {tasks.List.Count(j => j.State is BackgroundTaskState.Running or BackgroundTaskState.Queued)} running / queued\nWaiting · {tasks.List.Count(j => j.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval)}  /tasks");
                if (screen != null) screen.TaskNotice = () => (tasks.List.Count(j => j.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval) is var count && count > 0 ? L.Pick($"后台待处理 {count} · /tasks | ", $"{count} background items need attention · /tasks | ") : "") +
                    (runs.Active.Count(run => run.ConversationId != conversation.Id) is var others && others > 0 ? L.Pick($"后台会话 {others} 个运行中 | ", $"{others} background conversations running | ") : "") +
                    (runs.Find(conversation.Id) is { Waiting: true } waitingRun ? waitingRun.State switch { SessionRunState.AwaitingApproval => L.Pick("本会话等待授权 | ", "This conversation awaits approval | "), SessionRunState.AwaitingPlan => L.Pick("本会话等待计划审批 | ", "This conversation awaits plan approval | "), _ => L.Pick("本会话等待回答 | ", "This conversation awaits an answer | ") } : "") +
                    (ui.Inbox?.Label is { Length: > 0 } inboxLabel ? inboxLabel + " | " : "");
                if (screen != null) screen.TaskLookup = tasks.Get;
                if (screen != null) screen.InvocationTasks = id => tasks.List.Where(task => task.ParentToolCallId == id).OrderBy(task => task.CreatedAt).ToArray();
                if (screen != null) screen.TaskRevision = () => tasks.Revision;
                if (screen != null) screen.Runs = runs;
                if (screen != null) screen.RunRevision = () => runs.Revision + TerminalHosts.All.Sum(workspace => workspace.Runs.Revision);
                if (screen != null) screen.OtherWorkspaces = () => TerminalHosts.All
                    .Where(workspace => !string.Equals(workspace.DataDirectory, dataDir, StringComparison.OrdinalIgnoreCase) && workspace.Runs.Any)
                    .Select(workspace => (Name: Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace.Workspace)) is { Length: > 0 } name ? name : workspace.Workspace,
                        Running: workspace.Runs.Count, Waiting: workspace.Runs.Active.Count(run => run.Waiting)))
                    .ToArray();
                if (screen != null)
                {
                    var taskViews = new Dictionary<string, TaskTranscriptView>();
                    screen.TaskTranscript = task =>
                    {
                        if (!taskViews.TryGetValue(task.Id, out var view)) taskViews[task.Id] = view = new(store, tasks, task.Id, () => ui.ShowThinking);
                        return view;
                    };
                    screen.TaskPreview = task => TaskTranscriptView.LatestStep(store, tasks, task, ui.ShowThinking);
                }
                ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? L.Pick("尚未选择模型 · 管理功能可直接使用", "No model selected · management commands still work"), conversation.Id);
                if (restartWebUi)
                {
                    try
                    {
                        await WebUiAsync([], openBrowser: false, port: restartWebUiPort);
                        ManagementUi.Success(ui, L.Pick("工作区已切换，Web 工作台已在新工作区重启（浏览器会自动重连）。", "Workspace switched; the Web workspace restarted there (the browser reconnects automatically)."));
                    }
                    catch (Exception ex) { ui.Error(L.Pick("Web 工作台自动重启失败：", "Failed to restart the Web workspace: ") + ex.Message); }
                }
                ui.RunningCommand = async (command, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var words = CommandLineWords.Parse(command);
                    switch (words[0])
                    {
                        case "/tasks": await taskManager.ExecuteAsync(words.Skip(1).ToArray(), ct); break;
                        case "/help": ChatHelp(catalog, words.ElementAtOrDefault(1) == "all"); break;
                        case "/status":
                            TerminalOutput.Write(new Panel(new Text(L.Pick($"工作区：{options.WorkspaceRoot}\n会话：{conversation.Id}\n模型：{config?.DefaultModel}\n权限：{approval.ModeLabel}\n计划：{PlanStatus(store.GetPlan(conversation.Id))}\n{ui.ContextLabel}\n本会话正在运行：{runs.IsRunning(conversation.Id)}\n其他后台会话：{runs.Active.Count(run => run.ConversationId != conversation.Id)} 个\n运行任务：{tasks.List.Count(task => BackgroundTasks.IsActive(task.State))}", $"Workspace: {options.WorkspaceRoot}\nConversation: {conversation.Id}\nModel: {config?.DefaultModel}\nPermission: {approval.ModeLabel}\nPlan: {PlanStatus(store.GetPlan(conversation.Id))}\n{ui.ContextLabel}\nThis conversation running: {runs.IsRunning(conversation.Id)}\nOther background conversations: {runs.Active.Count(run => run.ConversationId != conversation.Id)}\nActive tasks: {tasks.List.Count(task => BackgroundTasks.IsActive(task.State))}"))).Header(L.Pick("执行中 · 会话状态", "Running · conversation status"))); break;
                        case "/sandbox":
                            if (words.Length > 2 || (words.Length == 2 && !string.Equals(words[1], "status", StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException(L.Pick("请先停止当前任务，再切换沙箱。", "Stop the current task before switching the sandbox."));
                            ShowSandbox(options, CurrentRuntime(), ui); break;
                        case "/browser":
                            if (words.Length > 2 || words.ElementAtOrDefault(1) is not (null or "on" or "off" or "status")) throw new ArgumentException(L.Pick("任务执行中支持 /browser on|off|status；连接请先停止任务。", "While a task runs, use /browser on|off|status; stop the task before connecting."));
                            await browser.ExecuteAsync(words.Skip(1).ToArray()); break;
                        // Switching sessions while a task runs is the point of background runs: the
                        // current session keeps working and the view moves on.
                        case "/new": case "/clear":
                            Navigate(SessionOperations.Get(store, null, options.WorkspaceRoot)); ui.DetachRequested = true; break;
                        case "/session": case "/sessions": case "/resume":
                            if (words.Length == 1) { SessionOperations.List(store, ui, null, runs); break; }
                            Navigate(SessionOperations.Get(store, words[1], options.WorkspaceRoot)); ui.DetachRequested = true; break;
                        case "/parent": Navigate(SessionOperations.Parent(store, conversation)); ui.DetachRequested = true; break;
                        case "/children":
                            if (words.Length == 1) { foreach (var child in SessionOperations.Children(store, conversation.Id)) TerminalOutput.Write(new Text(child.Id.ToString()[..8] + " · " + child.AgentRole + " · " + SessionOperations.SingleLineTitle(child.Title) + SessionOperations.RunLabel(runs, child.Id) + "\n")); break; }
                            Navigate(SessionOperations.Child(store, conversation, words[1], ui)); ui.DetachRequested = true; break;
                        case "/rename":
                            conversation.Title = words.ElementAtOrDefault(1) ?? throw new ArgumentException(L.Pick("用法：/rename 标题。", "Usage: /rename title."));
                            store.UpdateConversation(conversation); ManagementUi.Success(ui, L.Pick("会话已重命名。", "Conversation renamed.")); break;
                        case "/copy":
                            var copyText = words.ElementAtOrDefault(1) switch
                            {
                                null => SessionTranscript.Format(conversation),
                                "last" => SessionTranscript.LastReply(store.GetVisibleMessages(conversation.Id)),
                                "id" => conversation.Id.ToString(),
                                _ => throw new ArgumentException(L.Pick("用法：/copy [last|id]。", "Usage: /copy [last|id]."))
                            };
                            if (!ClipboardText.Write(copyText)) throw new IOException(L.Pick("剪贴板写入失败，请重试或使用 /export。", "Clipboard write failed; retry or use /export."));
                            ManagementUi.Success(ui, L.Pick("已复制到剪贴板。", "Copied to the clipboard.")); break;
                        case "/export":
                            var exportPath = Path.Combine(Path.GetTempPath(), "session-" + Guid.NewGuid().ToString("N") + ".md");
                            await SessionTranscript.ExportAsync(conversation, exportPath, words[1] != "--no-thinking", words[1] != "--no-tools");
                            ManagementUi.Success(ui, L.Pick("会话已导出：", "Conversation exported: ") + Path.GetFullPath(exportPath)); break;
                        case "/todo": TodoCommand.Execute(store, ui, conversation.Id, words.Skip(1).ToArray()); break;
                        case "/context": SessionOperations.Context(conversation, options.MaxContextCharacters); break;
                        case "/thinking": ui.ShowThinking = !ui.ShowThinking; break;
                        case "/details": ui.Verbose = !ui.Verbose; break;
                        case "/web-ui" when words.ElementAtOrDefault(1) is null or "status" or "stop" or "qr"
                            || words.ElementAtOrDefault(1)?.StartsWith("--", StringComparison.Ordinal) == true:
                            await WebUiAsync(words.Skip(1).ToArray()); break;
                        default: throw new ArgumentException(L.Pick("此命令需要在当前任务停止后执行。运行中可用 /new、/session [ID]、/children、/todo、/status、/copy、/export、/tasks、/help、/thinking、/details、/queue、/steer 和 /web-ui status；Esc 停止任务。", "This command needs the current task stopped. While running use /new, /session [ID], /children, /todo, /status, /copy, /export, /tasks, /help, /thinking, /details, /queue, /steer and /web-ui status; Esc stops the task."));
                    }
                };
                if (!args.Flags.Contains("no-initialize") && store.GetSetting("environment:initialized") != "1")
                {
                    try
                    {
                        options = await CancelableInitializationAsync();
                    }
                    catch (EnvironmentRestartRequiredException ex)
                    { ManagementUi.Success(ui, ex.Message); return 0; }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
                    { ui.Error(L.Pick("首次环境初始化未完成：", "Initial environment setup did not finish: ") + ex.Message + L.Pick("。可使用 /doctor install 重试。", ". Retry with /doctor install.")); }
                }
                if (conversation.Revert is { } stagedRevert)
                    ui.Draft = await SessionTimeline.RestoreDraftAsync(store.GetMessages(conversation.Id).Single(message => message.Id == stagedRevert.MessageId));
                var editor = new InputEditor(Path.Combine(dataDir, "terminal-history.json"), options.WorkspaceRoot, () => CommandCatalog.Builtins.Concat(plugins.Commands.Select(c => new CommandHint("/ext " + c.Name, c.Description))).Concat(catalog.List().Where(s => s.Enabled && s.UserInvocable).Select(s => new CommandHint("/" + s.Name, s.Description, "任务参数", IsSkill: true))));
                editor.WakeRequested = () => conversation.Revert == null && tasks.PendingNotifications(conversation.Id).Count > 0;
                while (true)
                {
                    // A session that kept working while the view was elsewhere is picked up exactly where it is.
                    if (runs.Find(conversation.Id) is { IsFinished: false, Attached: false } liveRun)
                    {
                        try { await ui.AttachRunAsync(liveRun, CancellationToken.None); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { ui.Error(ex.Message); }
                        continue;
                    }
                    foreach (var finished in runs.TakeFinished())
                        ManagementUi.Success(ui, L.Pick("后台会话已完成 · ", "Background conversation finished · ") + SessionOperations.SingleLineTitle(finished.Title) +
                            (finished.Failure != null ? " · " + finished.Failure : finished.FinishReason is null or "stop" or "STOP" ? "" : " · " + finished.FinishReason));
                    if (!tasks.IsSessionRunning(conversation.Id) && !runs.IsRunning(conversation.Id) && ui.Inbox?.Next is { } pendingInput)
                    {
                        try
                        {
                            var pendingEngine = await EngineAsync();
                            await RunTurnAsync(runs, pendingEngine, conversation.Id, pendingInput.Content, config!, ui, checkpoints, store, pendingInput: pendingInput);
                        }
                        catch (OperationCanceledException) { ui.Error(L.Pick("待处理输入执行已取消。", "Pending input execution was cancelled.")); }
                        catch (Exception ex) { ui.Error(ex.Message); }
                        if (ui.Inbox.Pending.Any(item => item.Id == pendingInput.Id))
                        {
                            ui.Inbox.AutomaticDeliveryPaused = true;
                            ui.Error(L.Pick("待处理输入已保留；修复启动问题后 /queue run 重试，或 /queue edit|delete ID。", "Pending input was kept; fix the startup problem and retry with /queue run, or use /queue edit|delete ID."));
                        }
                        continue;
                    }
                    if (conversation.Revert == null && !runs.IsRunning(conversation.Id) && tasks.PendingNotifications(conversation.Id).Count > 0)
                    {
                        var results = SubAgentNotifications.Deliver(store, tasks, conversation.Id);
                        if (results.Count > 0)
                        {
                            try
                            {
                                ManagementUi.Success(ui, L.Pick("收到子代理完成结果 · 正在继续父任务。", "Received child-agent results · continuing the parent task."));
                                var resumedEngine = await EngineAsync();
                                await RunTurnAsync(runs, resumedEngine, conversation.Id, "Review the newly delivered delegated task results and continue the existing parent objective. Verify claims before relying on them.", config!, ui, checkpoints, store, internalMessage: true);
                            }
                            catch (OperationCanceledException) { ui.Error(L.Pick("父任务继续处理已取消；子代理结果已保存。", "Continuing the parent task was cancelled; child-agent results are saved.")); }
                            catch (Exception ex) { ui.Error(ex.Message); }
                        }
                        continue;
                    }
                    ui.SetThinkingEffort(config);
                    var text = await editor.ReadAsync(L.Pick(
                        $"{config?.DisplayName ?? "未选择提供商"} · {config?.DefaultModel ?? "按需配置"} · 思考 {config?.ReasoningEffort ?? "default"} · {approval.ModeLabel} · 会话 {conversation.Id.ToString()[..8]}{runs.Label(conversation.Id)} · {ui.Inbox?.Label}",
                        $"{config?.DisplayName ?? "No provider selected"} · {config?.DefaultModel ?? "configure on demand"} · reasoning {config?.ReasoningEffort ?? "default"} · {approval.ModeLabel} · session {conversation.Id.ToString()[..8]}{runs.Label(conversation.Id)} · {ui.Inbox?.Label}"), initial: ui.Draft);
                    if (editor.InterruptedDraft != null) { ui.Draft = editor.InterruptedDraft; editor.InterruptedDraft = null; }
                    else if (screen == null) ui.Draft = "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    try
                    {
                        PromptReferences.ExpandedPrompt? preparedPrompt = null;
                        if (text.StartsWith('/'))
                        {
                            var originalCommand = text;
                            var firstWord = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
                            var words = firstWord == "/workspace" ? WorkspaceNavigation.ParseCommand(text) : CommandLineWords.Parse(text);
                            var command = words[0]; var rest = words.Skip(1).ToArray(); var tail = text.Length > command.Length ? text[(command.Length + 1)..].Trim() : "";
                            switch (command)
                            {
                                case "/workspace":
                                    if (rest.Length == 0)
                                    {
                                        TerminalOutput.Write(new Panel(new Text(options.WorkspaceRoot)).Header(L.Pick("当前工作目录 · /workspace 路径 切换", "Current workspace · /workspace path to switch")));
                                        continue;
                                    }
                                    var targetWorkspace = WorkspaceNavigation.Resolve(string.Join(' ', rest), options.WorkspaceRoot, approval.Permission);
                                    if (SessionOperations.InWorkspace(new Conversation { WorkspaceRoot = options.WorkspaceRoot }, targetWorkspace))
                                    { ManagementUi.Success(ui, L.Pick("当前已在此工作目录：", "Already in this workspace: ") + targetWorkspace); continue; }
                                    // Background runs and jobs belong to their workspace host and keep
                                    // going here; the terminal just moves its screen to the other workspace.
                                    var nextArguments = WorkspaceNavigation.Arguments(raw, args, targetWorkspace, approval.Permission);
                                    var nextDirectory = TerminalArguments.Parse(nextArguments).DataDirectory;
                                    DataDirectoryLease.EnsureNoLegacyWriter(nextDirectory);
                                    using (var targetStore = new DataStore(nextDirectory)) { }
                                    if (config == null && args.Get("config") != null)
                                        config = await models.SelectAsync(args.Get("config"), args.Get("model"));
                                    var previousArguments = WorkspaceNavigation.Arguments(raw, args, options.WorkspaceRoot, approval.Permission, conversation.Id);
                                    throw new WorkspaceChange(nextArguments, previousArguments, config, approval.PlanMode, approval.SimpleMode, webUi != null, webUi?.Port ?? 0);
                                case "/help": ChatHelp(catalog, rest.FirstOrDefault() == "all"); continue;
                                case "/stats": UsageStatistics.Show(store, ui); continue;
                                case "/timeline":
                                    if (rest.FirstOrDefault() == "list" || rest.Length == 0 && screen == null)
                                    {
                                        var rows = new Table().AddColumn(L.Pick("消息 ID", "Message ID")).AddColumn(L.Pick("时间", "Time")).AddColumn(L.Pick("用户输入", "User input"));
                                        foreach (var message in SessionTimeline.Entries(store, conversation.Id)) rows.AddRow(message.Id.ToString()[..8], message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"), Markup.Escape(SessionTimeline.Text(message)));
                                        TerminalOutput.Write(rows); continue;
                                    }
                                    var timelineChoice = rest.Length == 0 ? await screen!.TimelineAsync() :
                                        new FullScreenTerminal.TimelineChoice(SessionTimeline.Find(store, conversation.Id, ManagementUi.Require(rest, 1, L.Pick("消息 ID", "message ID"))).Id, rest[0]);
                                    if (timelineChoice == null) continue;
                                    var timelineMessage = SessionTimeline.Find(store, conversation.Id, timelineChoice.MessageId.ToString());
                                    switch (timelineChoice.Action)
                                    {
                                        case "jump": if (screen == null) throw new ArgumentException(L.Pick("消息定位需要全屏终端。", "Jumping to a message requires the full-screen terminal.")); screen.JumpToMessage(timelineMessage.Id); break;
                                        case "copy": if (!ClipboardText.Write(SessionTimeline.Text(timelineMessage))) throw new IOException(L.Pick("剪贴板写入失败。", "Clipboard write failed.")); ManagementUi.Success(ui, L.Pick("消息正文已复制。", "Message text copied.")); break;
                                        case "restore": ui.Draft = await SessionTimeline.RestoreDraftAsync(timelineMessage); break;
                                        case "revert":
                                            if (tasks.List.Any(job => BackgroundTasks.IsActive(job.State))) throw new InvalidOperationException(L.Pick("请先等待或停止后台任务，再撤回文件和历史。", "Wait for or stop background tasks before reverting files and history."));
                                            await checkpointManager.StageAsync(conversation, timelineMessage.Id); break;
                                        case "fork":
                                            var forkDraft = await SessionTimeline.RestoreDraftAsync(timelineMessage);
                                            var fork = SessionOperations.Branch(store, conversation, null, timelineMessage.Id);
                                            Navigate(fork); ui.Draft = forkDraft; break;
                                        default: throw new ArgumentException(L.Pick("用法：/timeline [list|jump|copy|restore|fork ID]", "Usage: /timeline [list|jump|copy|restore|fork ID]"));
                                    }
                                    continue;
                                case "/queue":
                                    ui.Draft = await ui.Inbox!.CommandAsync(text, ui.Draft) ?? ui.Draft; continue;
                                case "/steer":
                                    if (string.IsNullOrWhiteSpace(tail)) throw new ArgumentException(L.Pick("用法：/steer 任务说明", "Usage: /steer task instructions"));
                                    await ui.Inbox!.EnqueueAsync(tail, "steer"); continue;
                                case "/plugins":
                                    await CancelableAsync(ct => extensions.PluginsAsync(rest, ct));
                                    if (CurrentRuntime() is { } pluginRuntime)
                                    {
                                        pluginRuntime.Tools.Replace<PluginToolSet>((PluginToolSet)plugins.ToolSet());
                                        pluginRuntime.RefreshToolSurface();
                                    }
                                    continue;
                                case "/hooks": extensions.Hooks(rest); continue;
                                case "/login": await CancelableAsync(ct => extensions.LoginAsync(rest, ct)); if (rest.FirstOrDefault() is not ("status" or "list")) config = null; continue;
                                case "/logout": await CancelableAsync(ct => extensions.LogoutAsync(rest, ct)); config = null; continue;
                                case "/tasks": await taskManager.ExecuteAsync(rest); continue;
                                case "/agents": await roleManager.ExecuteAsync(rest); continue;
                                case "/background":
                                    if (string.IsNullOrWhiteSpace(tail)) throw new ArgumentException(L.Pick("用法：", "Usage: ") + command + L.Pick(" 任务描述。", " task description."));
                                    await EngineAsync(); var jobId = StartJob(tail, "后台任务", background: true).TaskId; ManagementUi.Success(ui, L.Pick("已启动 ", "Started ") + jobId[..8] + L.Pick("；完成后自动通知父会话，使用 /tasks 管理。", "; the parent conversation is notified on completion; manage with /tasks.")); continue;
                                case "/subagents":
                                    if (rest.FirstOrDefault() == "inherit")
                                    {
                                        args.Values.Remove("subagent-model"); args.Values.Remove("subagent-provider");
                                        store.SetSetting("subagents:model", ""); store.SetSetting("subagents:provider", "");
                                        ManagementUi.Success(ui, L.Pick("子代理默认跟随当前模型。", "Child agents now follow the current model by default."));
                                    }
                                    else if (rest.Length > 0)
                                    {
                                        config ??= await models.SelectAsync(args.Get("config"), args.Get("model"));
                                        var childModel = SubAgentModelSelection.Resolve(config, store.ApiConfigs, rest.ElementAtOrDefault(1), rest[0]);
                                        args.Values.Remove("subagent-model"); args.Values.Remove("subagent-provider");
                                        store.SetSetting("subagents:provider", childModel.Id.ToString());
                                        store.SetSetting("subagents:model", rest.ElementAtOrDefault(1) ?? "");
                                        ManagementUi.Success(ui, L.Pick("子代理默认模型：", "Default child-agent model: ") + childModel.DisplayName + " / " + childModel.DefaultModel);
                                    }
                                    else
                                    {
                                        var childProvider = args.Get("subagent-provider") ?? store.GetSetting("subagents:provider");
                                        var childDefault = args.Get("subagent-model") ?? store.GetSetting("subagents:model");
                                        childProvider = store.ApiConfigs.FirstOrDefault(c => c.Id.ToString() == childProvider)?.DisplayName ?? childProvider;
                                        TerminalOutput.Write(new Text(L.Pick("子代理默认：", "Child-agent default: ") + (string.IsNullOrWhiteSpace(childProvider) ? L.Pick("当前提供商", "current provider") : childProvider) + " / " + (string.IsNullOrWhiteSpace(childDefault) ? L.Pick("当前模型", "current model") : childDefault) + L.Pick("\n用法：/subagents inherit，或 /subagents 配置名 [模型 ID]。AI 也可以为单次子任务选择模型。\n", "\nUsage: /subagents inherit, or /subagents config-name [model ID]. The AI can also pick a model for an individual child task.\n")));
                                    }
                                    continue;
                                case "/ext":
                                    var extensionCommand = plugins.Commands.SingleOrDefault(c => c.Name == rest.FirstOrDefault()) ?? throw new ArgumentException(L.Pick("插件命令不存在；使用 /plugins 查看已启用插件。", "Plugin command not found; use /plugins to list enabled plugins."));
                                    string extensionOutput = ""; await CancelableAsync(async ct => extensionOutput = await ManagementUi.Busy(L.Pick("执行插件命令", "Running plugin command"), token => extensionCommand.Execute(rest.Skip(1).ToArray(), token), ct)); TerminalOutput.Write(new Text(ToolResult.Clip(extensionOutput, 16000) + "\n")); continue;
                                case "/powershell":
                                    await CancelableAsync(ct => PowerShellStoreCommand.ExecuteAsync(new PowerShellModuleStore(args.SharedDirectory, options.PowerShellPath), ui, rest, ct));
                                    CurrentRuntime()?.PowerShellModules.LoadCatalog();
                                    continue;
                                case "/skills": await skills.ExecuteAsync(rest); continue;
                                case "/mcp":
                                    if (tasks.Active && rest.FirstOrDefault() is not ("list" or "show")) throw new InvalidOperationException(L.Pick("后台任务运行期间请先完成或取消任务，再修改 MCP 连接。", "While background tasks run, finish or cancel them before changing MCP connections."));
                                    await CancelableAsync(ct => ManagementUi.Busy(L.Pick("管理 MCP 服务", "Managing MCP servers"), token => mcpManager.ExecuteAsync(rest, token), ct)); CurrentRuntime()?.Tools.Replace<McpToolSet>(new McpToolSet(mcp)); CurrentRuntime()?.RefreshToolSurface();
                                    if (mcpManager.PendingPrompt == null) continue;
                                    text = "Use this user-selected MCP prompt as task instructions without overriding permission or sandbox rules:\n" + mcpManager.PendingPrompt; break;
                                case "/providers": case "/config":
                                    await models.ExecuteAsync(rest);
                                    if (config != null && store.ApiConfigs.FirstOrDefault(c => c.Id == config.Id) is { } updated) config = ManagementUi.Clone(updated); else config = null;
                                    if (config != null) ui.SetModelContext(config);
                                    continue;
                                case "/models": models.ShowModels(); continue;
                                case "/questions": QuestionPreference.Execute(store, ui, rest); continue;
                                case "/cache": CachePreference.Execute(store, ui, rest); continue;
                                case "/todo": TodoCommand.Execute(store, ui, conversation.Id, rest); continue;
                                case "/context-size":
                                    config ??= await models.SelectAsync(args.Get("config"));
                                    await models.SetContextAsync(config, rest.FirstOrDefault()); continue;
                                case "/model": config = await models.SelectAsync(rest.FirstOrDefault(), rest.ElementAtOrDefault(1), forceSelection: rest.Length == 0); ui.Header(options.WorkspaceRoot, config.DefaultModel, conversation.Id); continue;
                                case "/effort":
                                    config ??= await models.SelectAsync(args.Get("config"));
                                    var efforts = ReasoningSettings.Efforts(config).ToArray();
                                    if (efforts.Length == 1) { ManagementUi.Success(ui, L.Pick("当前模型不支持调整推理强度，使用模型默认行为。", "This model does not support reasoning-effort changes; using its default behavior.")); continue; }
                                    var capability = ReasoningSettings.Capabilities(config);
                                    var effort = rest.FirstOrDefault() == "next" ? efforts[(Array.IndexOf(efforts, config.ReasoningEffort) + 1) % efforts.Length] : rest.FirstOrDefault() ?? ManagementUi.Choose(L.Pick("思考强度 · ", "Reasoning effort · ") + config.DefaultModel + " · " + capability.Protocol, efforts,
                                        level => level == "budget" ? L.Pick($"budget · 自定义 {capability.MinimumBudget}–{capability.MaximumBudget} tokens", $"budget · custom {capability.MinimumBudget}-{capability.MaximumBudget} tokens") : level);
                                    int? budget = rest.ElementAtOrDefault(1) is { } budgetText ? int.Parse(budgetText) : null;
                                    if (effort == "budget" && budget == null && rest.FirstOrDefault() == "next")
                                        budget = Math.Clamp(config.ThinkingBudget, capability.MinimumBudget, capability.MaximumBudget);
                                    if (effort == "budget" && budget == null && rest.FirstOrDefault() != "next")
                                        budget = int.Parse(await ManagementUi.Text(L.Pick($"思考预算（{capability.MinimumBudget}–{capability.MaximumBudget} tokens）", $"Reasoning budget ({capability.MinimumBudget}-{capability.MaximumBudget} tokens)"), Math.Clamp(config.ThinkingBudget, capability.MinimumBudget, capability.MaximumBudget).ToString()));
                                    var reasoningConfig = ManagementUi.Clone(config);
                                    ReasoningSettings.Configure(reasoningConfig, effort, budget);
                                    config = reasoningConfig;
                                    if (store.ApiConfigs.Any(c => c.Id == config.Id)) store.UpdateApiConfig(ManagementUi.Clone(config));
                                    ui.SetModelContext(config);
                                    ManagementUi.Success(ui, L.Pick("思考强度已设置：", "Reasoning effort set: ") + ReasoningSettings.Label(config) + " · " + capability.Protocol); continue;
                                case "/new": case "/clear": conversation = SessionOperations.Get(store, null, options.WorkspaceRoot); ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? L.Pick("按需配置", "configure on demand"), conversation.Id); continue;
                                case "/exit": case "/quit":
                                    throw new ChatExit(L.Pick("已退出聊天 · 会话 ", "Left chat · conversation ") + conversation.Id.ToString()[..8] + L.Pick(" 已保存 · 恢复：allagent --resume ", " saved · resume: allagent --resume ") + conversation.Id);
                                case "/session": case "/sessions": case "/resume":
                                    var resumed = rest.Length > 0 ? SessionOperations.Get(store, rest[0], options.WorkspaceRoot) : SessionOperations.Pick(store, ui, runs);
                                    Navigate(resumed); continue;
                                case "/parent": Navigate(SessionOperations.Parent(store, conversation)); continue;
                                case "/children": Navigate(SessionOperations.Child(store, conversation, rest.FirstOrDefault(), ui)); continue;
                                case "/copy":
                                    var copyText = rest.FirstOrDefault() switch
                                    {
                                        null => SessionTranscript.Format(conversation),
                                        "last" => SessionTranscript.LastReply(store.GetVisibleMessages(conversation.Id)),
                                        "id" => conversation.Id.ToString(),
                                        _ => throw new ArgumentException(L.Pick("用法：/copy [last|id]。", "Usage: /copy [last|id]."))
                                    };
                                    if (!ClipboardText.Write(copyText)) throw new IOException(L.Pick("剪贴板写入失败，请重试或使用 /export。", "Clipboard write failed; retry or use /export."));
                                    ManagementUi.Success(ui, L.Pick("已复制到剪贴板。", "Copied to the clipboard.")); continue;
                                case "/rename": conversation.Title = ManagementUi.Require(rest, 0, L.Pick("标题", "title")) + (rest.Length > 1 ? " " + string.Join(' ', rest.Skip(1)) : ""); store.UpdateConversation(conversation); ManagementUi.Success(ui, L.Pick("会话已重命名。", "Conversation renamed.")); continue;
                                case "/fork":
                                    Guid? forkBefore = null; string? forkName = null;
                                    if (rest.FirstOrDefault() == "--before")
                                    {
                                        forkBefore = SessionTimeline.Find(store, conversation.Id, ManagementUi.Require(rest, 1, L.Pick("消息 ID", "message ID"))).Id;
                                        if (rest.Length > 2) forkName = string.Join(' ', rest.Skip(2));
                                    }
                                    else if (rest.Length == 0)
                                    {
                                        var forkChoices = new[] { new SessionTimeline.ForkChoice(null, L.Pick("完整会话", "Full conversation")) }.Concat(SessionTimeline.Entries(store, conversation.Id)
                                            .Select(message => new SessionTimeline.ForkChoice(message.Id, message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + SessionTimeline.Text(message).Replace('\n', ' '))));
                                        forkBefore = ManagementUi.Choose(L.Pick("会话分支 · 选择历史边界", "Conversation branch · choose a history boundary"), forkChoices, item => item.Label).MessageId;
                                    }
                                    else forkName = tail;
                                    var preparedForkDraft = forkBefore is { } before ? await SessionTimeline.RestoreDraftAsync(SessionTimeline.Find(store, conversation.Id, before.ToString())) : null;
                                    Navigate(SessionOperations.Branch(store, conversation, forkName, forkBefore));
                                    if (preparedForkDraft != null) ui.Draft = preparedForkDraft; continue;
                                case "/branch": conversation = SessionOperations.Branch(store, conversation, tail.Length == 0 ? null : tail); ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? conversation.ModelId, conversation.Id); continue;
                                case "/history": SessionOperations.History(store, conversation.Id); continue;
                                case "/checkpoints": case "/diff": case "/undo": case "/rewind": case "/redo":
                                    if (command is "/undo" or "/rewind" or "/redo" && tasks.List.Any(job => BackgroundTasks.IsActive(job.State))) throw new InvalidOperationException(L.Pick("请先等待或停止后台任务，再还原文件和历史。", "Wait for or stop background tasks before restoring files and history."));
                                    await checkpointManager.ExecuteAsync(command, conversation, rest.FirstOrDefault()); continue;
                                case "/export":
                                    var exportPath = rest.FirstOrDefault(r => !r.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "session-" + Guid.NewGuid().ToString("N") + ".md");
                                    if (rest.Any(r => r.StartsWith("--") && r is not ("--no-thinking" or "--no-tools"))) throw new ArgumentException(L.Pick("导出选项：--no-thinking / --no-tools。", "Export options: --no-thinking / --no-tools."));
                                    await SessionTranscript.ExportAsync(conversation, exportPath, !rest.Contains("--no-thinking"), !rest.Contains("--no-tools")); ManagementUi.Success(ui, L.Pick("会话已导出：", "Conversation exported: ") + Path.GetFullPath(exportPath)); continue;
                                case "/context": SessionOperations.Context(conversation, options.MaxContextCharacters); continue;
                                case "/compact":
                                    var compactEngine = await EngineAsync();
                                    await CancelableAsync(ct => ui.RunAgentAsync(compactEngine.CompactAsync(conversation.Id, config!, tail, ct), ct));
                                    continue;
                                case "/plan":
                                    approval.PlanMode = !approval.PlanMode;
                                    if (approval.PlanMode) approval.SimpleMode = false;
                                    options = options with { Permission = approval.PlanMode ? PermissionMode.Ask : approval.Permission, SimpleMode = approval.SimpleMode, CodeTimeout = args.Get("code-timeout") == null ? TimeSpan.FromSeconds(approval.PlanMode ? 3600 : approval.Permission == PermissionMode.Ask ? 60 : 600) : options.CodeTimeout };
                                    if (!TerminalHosts.All.Any(workspace => workspace.Busy)) await DisposeRuntimesAsync();
                                    if (webUi != null) await webUi.RefreshRuntimeAsync();
                                    ManagementUi.Success(ui, L.Pick("权限模式：", "Permission mode: ") + approval.ModeLabel); continue;
                                case "/simple":
                                    approval.SimpleMode = !approval.SimpleMode;
                                    if (approval.SimpleMode) approval.PlanMode = false;
                                    options = options with { Permission = approval.Permission, SimpleMode = approval.SimpleMode };
                                    if (!TerminalHosts.All.Any(workspace => workspace.Busy)) await DisposeRuntimesAsync();
                                    if (webUi != null) await webUi.RefreshRuntimeAsync();
                                    ManagementUi.Success(ui, L.Pick("权限模式：", "Permission mode: ") + approval.ModeLabel); continue;
                                case "/permissions":
                                    if (rest.FirstOrDefault() == "directories")
                                    {
                                        if (rest.ElementAtOrDefault(1) == "clear") options.ExternalDirectoryPermissions.Clear(conversation.Id);
                                        var directories = options.ExternalDirectoryPermissions.List(conversation.Id);
                                        TerminalOutput.Write(new Panel(new Text(directories.Count == 0 ? L.Pick("此会话未记住外部目录。", "No external directories are remembered for this conversation.") : string.Join('\n', directories))).Header(L.Pick("外部目录权限 · 此会话", "External directory permissions · this conversation"))); continue;
                                    }
                                    var mode = rest.FirstOrDefault();
                                    if (mode == null && ui.Interactive) mode = ManagementUi.Choose(L.Pick("权限模式", "Permission mode"), new[] { "restricted", "ask", "full", "plan", "simple" }, value => value switch
                                    {
                                        "restricted" => L.Pick("限制模式 · 工作区内读写免审 · 工作区外需授权", "Restricted · free workspace reads/writes · approval outside"),
                                        "full" => L.Pick("完全访问 · 不逐次审核 · 可访问工作区外文件", "Full access · no per-call approval · files outside the workspace allowed"),
                                        "plan" => L.Pick("计划模式 · 只读调研 → 计划审批 → 自动执行", "Plan mode · read-only research → plan approval → automatic execution"),
                                        "simple" => L.Pick("简单模式 · 仅保留 PowerShell、读文件、改文件三个工具", "Simple mode · only PowerShell, read file and edit file"),
                                        _ => L.Pick("逐次授权", "Ask each time")
                                    });
                                    if (mode != null) await SetPermissionModeAsync(mode);
                                    TerminalOutput.Write(new Panel(new Text(approval.ModeLabel + "\n" + (approval.PlanMode
                                        ? L.Pick("文件读取可用；写入、Shell 和 MCP 操作停用。", "File reads are allowed; writes, shell and MCP operations are disabled.")
                                        : approval.SimpleMode
                                        ? L.Pick("仅保留 PowerShell、读文件与改文件三个工具；技能、MCP、子代理、插件、浏览器与桌面工具停用，新建文件用 PowerShell。审批沿用当前权限。", "Only PowerShell, read file and edit file remain; skills, MCP, child agents, plugins, browser and desktop tools are disabled. Create files with PowerShell. Approvals follow the current permission.")
                                        : approval.Permission switch
                                        {
                                            PermissionMode.Full => L.Pick("文件、Shell、MCP 与插件工具不再逐次审核；允许工作区外的文件和已安装可执行程序。\n双击 Esc / Ctrl+C 仍可停止任务。", "File, shell, MCP and plugin tools no longer require per-call approval; files outside the workspace and installed executables are allowed.\nDouble Esc / Ctrl+C still stops the task."),
                                            PermissionMode.Restricted => L.Pick("工作区内的文件读写直接执行；工作区外的路径、未信任的程序与 MCP / 桌面 / 插件工具执行前询问；可信 Shell 别名免逐次确认。", "Reads and writes inside the workspace run directly; paths outside, untrusted programs and MCP / desktop / plugin tools ask before running; trusted shell aliases skip approval."),
                                            _ => L.Pick("文件写入、未信任的 Shell 程序和 MCP 操作执行前显示参数并询问；可信 Shell 别名免逐次确认。", "File writes, untrusted shell programs and MCP operations show their arguments and ask first; trusted shell aliases skip approval.")
                                        }))).Header(L.Pick("当前权限", "Current permission"))); continue;
                                case "/sandbox":
                                    if (rest.Length > 1) throw new ArgumentException(L.Pick("用法：/sandbox [status|auto|hyperlight|monty]。", "Usage: /sandbox [status|auto|hyperlight|monty]."));
                                    var sandboxChoice = rest.FirstOrDefault()?.ToLowerInvariant();
                                    if (sandboxChoice is null or "status") { ShowSandbox(options, CurrentRuntime(), ui); continue; }
                                    await SetSandboxModeAsync(sandboxChoice);
                                    ManagementUi.Success(ui, L.Pick("沙箱选择已保存：", "Sandbox choice saved: ") + sandboxChoice + L.Pick("，下次任务执行前验证并启用。", "; it will be verified and enabled before the next task."));
                                    ShowSandbox(options, CurrentRuntime(), ui); continue;
                                case "/tools": if (CurrentRuntime() == null) await EngineAsync(); ShowTools(CurrentRuntime()!.Tools.Definitions, ui, CurrentRuntime()!.CodeAct.Surface); continue;
                                case "/browser": await browser.ExecuteAsync(rest); continue;
                                case "/web-ui": await WebUiAsync(rest); continue;
                                case "/computer": computer.Execute(rest); continue;
                                case "/status":
                                    TerminalOutput.Write(new Panel(new Text(L.Pick($"工作区：{options.WorkspaceRoot}\n会话：{conversation.Id}\n模型：{config?.DisplayName ?? "未选择"} / {config?.DefaultModel}\n思考：{config?.ReasoningEffort ?? "default"}\n权限：{approval.ModeLabel}\n计划：{PlanStatus(store.GetPlan(conversation.Id))}\n沙箱：{(CurrentRuntime()?.SandboxName is { } sandboxName ? sandboxName + " 已验证" : "首次任务时检查")}\n技能：{catalog.List().Count(s => s.Enabled)}\nMCP：{mcp.GetAllServerTools().Count} 个已连接\n后台会话：{runs.Active.Count} 个运行中\n审计：{options.AuditPath}", $"Workspace: {options.WorkspaceRoot}\nConversation: {conversation.Id}\nModel: {config?.DisplayName ?? "not selected"} / {config?.DefaultModel}\nReasoning: {config?.ReasoningEffort ?? "default"}\nPermission: {approval.ModeLabel}\nPlan: {PlanStatus(store.GetPlan(conversation.Id))}\nSandbox: {(CurrentRuntime()?.SandboxName is { } sandboxNameEn ? sandboxNameEn + " verified" : "checked on the first task")}\nSkills: {catalog.List().Count(s => s.Enabled)}\nMCP: {mcp.GetAllServerTools().Count} connected\nBackground conversations: {runs.Active.Count} running\nAudit: {options.AuditPath}"))).Header(L.Pick("会话状态", "Conversation status"))); continue;
                                case "/install-pip":
                                    throw new ArgumentException(L.Pick("pip 清单功能已移除，请使用 /powershell 搜索和安装 PowerShell 模块。", "The pip manifest feature was removed; use /powershell to search for and install PowerShell modules."));
                                case "/doctor":
                                    if (rest.FirstOrDefault() == "install") options = await CancelableInitializationAsync();
                                    await DoctorAsync(args, options, ui); continue;
                                case "/language":
                                    if (rest.Length > 1 || rest.FirstOrDefault() is { } languageArg && languageArg is not ("auto" or "zh" or "en"))
                                        throw new ArgumentException(L.Pick("用法：/language [auto|zh|en]。", "Usage: /language [auto|zh|en]."));
                                    ApplyLanguage(rest.FirstOrDefault() ?? ManagementUi.Choose(L.Pick("界面语言", "Interface language"), new[] { "auto", "zh", "en" }, LanguageLabel));
                                    continue;
                                case "/settings":
                                    if (rest.Length == 0)
                                    {
                                        var setting = ManagementUi.Choose(L.Pick("显示、语言与主题设置", "Display, language and theme settings"), new[]
                                        {
                                            (Id: "details", Label: L.Pick("工具详情", "Tool details")),
                                            (Id: "thinking", Label: L.Pick("思考内容", "Reasoning content")),
                                            (Id: "theme", Label: L.Pick("界面主题", "Interface theme")),
                                            (Id: "spinner", Label: L.Pick("加载指示", "Loading indicator")),
                                            (Id: "language", Label: L.Pick("界面语言", "Interface language")),
                                            (Id: "back", Label: L.Pick("返回聊天", "Back to chat")),
                                        }, item => item.Label);
                                        switch (setting.Id)
                                        {
                                            case "back": continue;
                                            case "spinner":
                                                LoadingIndicator.Set(ManagementUi.Choose(L.Pick("加载指示样式 · 当前 ", "Loading indicator style · current ") + LoadingIndicator.Name, LoadingIndicator.Names, name => name));
                                                store.SetSetting("terminal:spinner", LoadingIndicator.Name);
                                                ManagementUi.Success(ui, L.Pick("加载指示已保存：", "Loading indicator saved: ") + LoadingIndicator.Name); continue;
                                            case "thinking":
                                                ui.ShowThinking = ManagementUi.Choose(L.Pick("显示思考内容", "Show reasoning content"), L.Pick("关闭", "Off"), L.Pick("开启", "On")) == L.Pick("开启", "On");
                                                store.SetSetting("terminal:thinking", ui.ShowThinking ? "on" : "off"); continue;
                                            case "theme":
                                                var selectedTheme = ManagementUi.Choose(L.Pick("界面主题", "Interface theme"), TerminalTheme.All, t => t.Name);
                                                store.SetSetting("terminal:theme", selectedTheme.Id); if (screen != null) screen.Theme = selectedTheme; continue;
                                            case "language":
                                                var languageChoice = ManagementUi.Choose(L.Pick("界面语言", "Interface language"), new[] { "auto", "zh", "en" }, LanguageLabel);
                                                ApplyLanguage(languageChoice); continue;
                                            default:
                                                ui.Verbose = ManagementUi.Choose(L.Pick("显示工具详情", "Show tool details"), L.Pick("关闭", "Off"), L.Pick("开启", "On")) == L.Pick("开启", "On");
                                                break;
                                        }
                                    }
                                    else if (rest.Length == 2 && rest[0] == "verbose" && rest[1] is "on" or "off") ui.Verbose = rest[1] == "on";
                                    else if (rest.Length == 2 && rest[0] == "language" && rest[1] is "auto" or "zh" or "en") { ApplyLanguage(rest[1]); continue; }
                                    else throw new ArgumentException(L.Pick("用法 /settings verbose on|off|language auto|zh|en。", "Usage: /settings verbose on|off|language auto|zh|en."));
                                    store.SetSetting("terminal:verbose", ui.Verbose ? "on" : "off"); ManagementUi.Success(ui, L.Pick("终端设置已保存。", "Terminal settings saved.")); continue;
                                case "/themes":
                                    var theme = rest.FirstOrDefault() is { } themeId ? TerminalTheme.All.Single(t => t.Id == themeId) : ManagementUi.Choose("界面主题", TerminalTheme.All, t => t.Name);
                                    store.SetSetting("terminal:theme", theme.Id); if (screen != null) screen.Theme = theme; ManagementUi.Success(ui, L.Pick("主题已保存：", "Theme saved: ") + theme.Name); continue;
                                case "/spinner":
                                    if (rest.FirstOrDefault() == "list") { TerminalOutput.Write(new Text(string.Join(" · ", LoadingIndicator.Names))); continue; }
                                    var spinner = rest.FirstOrDefault() ?? ManagementUi.Choose(L.Pick("加载指示样式 · 当前 ", "Loading indicator style · current ") + LoadingIndicator.Name, LoadingIndicator.Names, name => name);
                                    LoadingIndicator.Set(spinner); store.SetSetting("terminal:spinner", LoadingIndicator.Name); ManagementUi.Success(ui, L.Pick("加载指示已保存：", "Loading indicator saved: ") + LoadingIndicator.Name); continue;
                                case "/thinking": ui.ShowThinking = !ui.ShowThinking; store.SetSetting("terminal:thinking", ui.ShowThinking ? "on" : "off"); ManagementUi.Success(ui, ui.ShowThinking ? L.Pick("显示模型提供的思考内容。", "Showing reasoning content from the model.") : L.Pick("隐藏思考内容；任务状态仍显示。", "Reasoning hidden; task status still shows.")); continue;
                                case "/details": ui.Verbose = !ui.Verbose; store.SetSetting("terminal:verbose", ui.Verbose ? "on" : "off"); ManagementUi.Success(ui, ui.Verbose ? L.Pick("工具详情已开启。", "Tool details enabled.") : L.Pick("工具详情已关闭。", "Tool details disabled.")); continue;
                                case "/memory": Memory(options.WorkspaceRoot, rest, ui); continue;
                                case "/init": Init(options.WorkspaceRoot, ui); continue;
                                default:
                                    if (catalog.List().Any(s => s.Enabled && s.UserInvocable && "/" + s.Name == command))
                                    {
                                        await CancelableAsync(async ct => preparedPrompt = await PromptReferences.ExpandSkillAsync(catalog, command[1..], tail, options.WorkspaceRoot, ct));
                                        text = preparedPrompt!.Text;
                                    }
                                    else
                                    {
                                        ui.Error(L.Pick("未知命令；输入 / 查看提示面板，或 /help。", "Unknown command; type / for suggestions or /help.")); continue;
                                    }
                                    break;
                            }
                            screen?.UserMessage(originalCommand);
                        }
                        preparedPrompt ??= await PromptReferences.ExpandWithImagesAsync(text, options.WorkspaceRoot, allowExplicitExternalImages: true);
                        if (runs.IsRunning(conversation.Id))
                        {
                            // The session is already working: text is guidance for that run, never a second turn.
                            store.EnqueueInput(conversation.Id, text, preparedPrompt.Text, preparedPrompt.Images, "steer");
                            ManagementUi.Success(ui, L.Pick("消息已作为指导发送，将继续当前会话任务。", "Message sent as guidance; the current conversation task will continue."));
                            continue;
                        }
                        if (tasks.TryPostInput(conversation.Id, () => store.EnqueueInput(conversation.Id, text, preparedPrompt.Text, preparedPrompt.Images, "steer")))
                        {
                            ManagementUi.Success(ui, L.Pick("消息已接收，将继续当前会话任务。", "Message received; the current conversation task will continue."));
                            continue;
                        }
                        var engine = await EngineAsync(); await RunTurnAsync(runs, engine, conversation.Id, text, config!, ui, checkpoints, store, preparedPrompt: preparedPrompt);
                    }
                    catch (WorkspaceChange) { throw; }
                    catch (ChatExit) { throw; }
                    catch (OperationCanceledException) { TerminalOutput.MarkupLine(L.Pick("[dim]操作已取消；可以继续输入。[/]", "[dim]Operation cancelled; you can keep typing.[/]")); }
                    catch (Exception ex) { ui.Error(ex.Message); }
                }
            }
            finally
            {
                // Host-owned services (store, jobs, runs, sandboxes, MCP) stay alive across a workspace
                // switch; only the screen-scoped objects built above are released here.
                if (webUi != null) await webUi.DisposeAsync();
            }
        }
        catch (WorkspaceChange) { throw; }
        catch (ChatExit exit) { TerminalOutput.Write(new Text("\n" + exit.Message + "\n", new Style(Color.Grey))); return 0; }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) { if (ui != null) ui.Error(ex.Message); else Console.Error.WriteLine(ex.Message); return 1; }
    }
    /// <summary>
    /// Stable host instruction block: project instructions and plan mode. The skill catalog is a
    /// tail snapshot owned by AgentEngine, so enabling a skill never rewrites this cached prefix.
    /// </summary>
    private static string PlanStatus(PlanDocument? plan) => plan == null
        ? L.Pick("无", "none")
        : (plan.Status switch { "approved" => L.Pick("已批准", "approved"), "rejected" => L.Pick("已拒绝", "rejected"), _ => L.Pick("待批准", "pending approval") }) + " · " + plan.Title;

    internal static string Instructions(string workspace, SkillCatalog catalog, bool plan, bool includeRoot = true)
    {
        _ = catalog;
        var parts = new List<string> { ProjectInstructions.Load(workspace, includeRoot) };
        if (plan) parts.Add("PLAN mode: research read-only. Do not request writes, shell, MCP or delegation. When ready, submit the complete Markdown plan through plan_propose and wait for the human decision. After approval the host switches to full auto-approval; execute the approved plan immediately.");
        return string.Join("\n\n", parts);
    }
    private static void Memory(string workspace, string[] args, TerminalUi ui)
    {
        var path = new WorkspacePolicy(workspace).Resolve(".allagent/AGENT.md");
        if (args.FirstOrDefault() == "set") { var content = string.Join(' ', args.Skip(1)); if (content.Length is 0 or > MaxProjectInstructionCharacters) throw new ArgumentException(L.Pick("项目指令需要 1–100000 字符。", "Project instructions must be 1-100000 characters.")); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); ManagementUi.Success(ui, L.Pick("项目指令已保存，下次任务自动加载。", "Project instructions saved; they load automatically on the next task.")); }
        else TerminalOutput.Write(new Panel(new Text(File.Exists(path) ? File.ReadAllText(path) : L.Pick("尚未设置；/init 创建模板，/memory set 内容 设置指令。工作区 AGENTS.md 也会自动加载。", "Not set; /init creates a template, /memory set content writes instructions. The workspace AGENTS.md also loads automatically."))).Header(L.Pick("项目指令 · ", "Project instructions · ") + Markup.Escape(path)));
    }
    private static void Init(string workspace, TerminalUi ui)
    {
        var path = new WorkspacePolicy(workspace).Resolve(".allagent/AGENT.md"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new StreamWriter(new FileStream(path, FileMode.CreateNew)); file.Write("# Project instructions\n\nDescribe the project, build/test commands, coding conventions and delivery requirements here.\n"); ManagementUi.Success(ui, L.Pick("项目指令模板已创建：", "Project-instructions template created: ") + path);
    }
    private static async Task RunTurnAsync(SessionRuns runs, AgentEngine engine, Guid id, string text, ApiConfig config, TerminalUi ui, WorkspaceCheckpoints checkpoints, DataStore store, bool internalMessage = false, PendingInput? pendingInput = null, PromptReferences.ExpandedPrompt? preparedPrompt = null)
    {
        var prompt = preparedPrompt ?? (pendingInput == null ? await PromptReferences.ExpandWithImagesAsync(text, ui.Workspace, CancellationToken.None, allowExplicitExternalImages: !internalMessage)
            : new PromptReferences.ExpandedPrompt(pendingInput.Content, pendingInput.Images));
        if (pendingInput == null && prompt.Images.Count > 0 && !ui.Json) TerminalOutput.Write(ImageAttachmentsView.Render(prompt.Images));
        // The run owns its lifecycle from here: it keeps working when the view moves to another
        // session, and it — not the view — closes the checkpoint and delivers the result.
        var run = runs.Start(store, checkpoints, id, engine, prompt.Text, config, internalMessage, prompt.Images, inputText: text, fromInbox: pendingInput != null);
        await ui.AttachRunAsync(run, CancellationToken.None);
        if (ui.Inbox?.Session == id && store.GetConversation(id)?.Revert == null) ui.Inbox.AutomaticDeliveryPaused = false;
    }
    internal static async Task CancelableAsync(Func<CancellationToken, Task> action)
    {
        using var source = new CancellationTokenSource(); ConsoleCancelEventHandler handler = (_, evt) => { evt.Cancel = true; source.Cancel(); }; Console.CancelKeyPress += handler;
        try { await action(source.Token); } finally { Console.CancelKeyPress -= handler; }
    }
    private static async Task<int> DoctorAsync(TerminalArguments args, AgentOptions options, TerminalUi ui)
    {
        var doctor = new EnvironmentDoctor(); if (args.Flags.Contains("install") && !ui.Interactive) throw new ArgumentException(L.Pick("安装需要交互终端逐项确认。", "Installation needs an interactive terminal to confirm each step."));
        var environment = args.Flags.Contains("install") ? await doctor.EnsureAsync(ConfirmAsync, options.PythonPath, options.PowerShellPath) : await doctor.CheckAsync(options.PythonPath, options.PowerShellPath);
        if (args.Flags.Contains("install"))
        {
            await doctor.EnsureRipgrepAsync();
            options = options with { PythonPath = environment.Python };
            environment = await doctor.CheckAsync(options.PythonPath, environment.PowerShell);
        }
        var ripgrep = EnvironmentDoctor.FindExecutable("rg.exe");
        await using var browserCheck = new BrowserUseTools(options.BrowserUse);
        var browserEnvironment = await browserCheck.CheckEnvironmentAsync();
        var powershell = new PowerShellModuleStore(args.SharedDirectory, environment.PowerShell);
        if (environment.PowerShell != null) await powershell.ExecuteAsync("list");
        ManagementUi.Output(ui, new { environment, ripgrep, browser = browserEnvironment, powershell = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(powershell.Summary) }, () =>
        {
            var table = new Table().AddColumn(L.Pick("环境", "Environment")).AddColumn(L.Pick("结果", "Result"));
            table.AddRow("Browser Use / WebView2", Markup.Escape(browserEnvironment.Ok ? browserEnvironment.ToJson() : browserEnvironment.Error ?? L.Pick("环境检查失败", "environment check failed")));
            table.AddRow(L.Pick("宿主 Python 3.10–3.14（沙箱运行依赖）", "Host Python 3.10-3.14 (required by the sandbox)"), Markup.Escape(environment.Python ?? L.Pick("缺失或版本不符", "missing or wrong version"))); table.AddRow(L.Pick("内嵌 PowerShell SDK", "Embedded PowerShell SDK"), Markup.Escape(environment.PowerShell ?? L.Pick("缺失", "missing")));
            table.AddRow("ripgrep", Markup.Escape(ripgrep ?? L.Pick("缺失", "missing")));
            table.AddRow(L.Pick("PowerShell 能力", "PowerShell support"), Markup.Escape(powershell.Summary));
            table.AddRow(L.Pick("执行模式", "Execution mode"), L.Pick("自动选择 Hyperlight / Monty · ", "Automatically select Hyperlight / Monty · ") + (options.Permission switch { PermissionMode.Full => L.Pick("完全访问 · 宿主工具免审批", "Full access · host tools need no approval"), PermissionMode.Restricted => L.Pick("限制模式 · 工作区内免审", "Restricted · workspace operations need no approval"), _ => L.Pick("逐次授权", "Ask each time") })); table.AddRow(L.Pick("工作区", "Workspace"), Markup.Escape(options.WorkspaceRoot));
            TerminalOutput.Write(table); foreach (var issue in environment.Problems) ui.Error(issue);
        });
        if (!environment.Ready) return 1;
        await using var runtime = await AgentRuntime.CreateAsync(options with { PythonPath = environment.Python, PowerShellPath = environment.PowerShell, AllowSandboxInstallation = args.Flags.Contains("install") }); var probe = await runtime.ProbeSandboxAsync();
        ManagementUi.Output(ui, new { sandbox = probe, backend = runtime.SandboxName, fallbackReason = runtime.SandboxFallbackReason }, () => { if (probe.Ok) TerminalOutput.Write(new Text("✓ " + runtime.SandboxName + L.Pick(" 实际执行校验通过\n", " execution verification passed\n"))); else ui.Error(probe.Error ?? L.Pick("执行环境失败", "Execution environment failed")); });
        if (probe.Ok) ReportSandbox(runtime, ui);
        return probe.Ok && browserEnvironment.Ok && ripgrep != null ? 0 : 1;
    }
    private static SandboxMode SavedSandboxMode(DataStore store) => store.GetSetting("terminal:sandbox") switch
    {
        "hyperlight" => SandboxMode.Hyperlight,
        "monty" => SandboxMode.Monty,
        _ => SandboxMode.Auto
    };

    private static void ShowSandbox(AgentOptions options, AgentRuntime? runtime, TerminalUi ui)
    {
        var mode = options.SandboxMode.ToString().ToLowerInvariant();
        var active = runtime?.SandboxName ?? L.Pick("尚未启动，下次任务时验证", "not started; verified on the next task");
        var behavior = options.SandboxMode == SandboxMode.Auto
            ? L.Pick("优先 Hyperlight，初始化失败自动切换 Monty。", "Prefer Hyperlight and fall back to Monty if initialization fails.")
            : L.Pick("仅使用指定沙箱；初始化失败时报告错误。", "Use only the selected sandbox; report an error if initialization fails.");
        ManagementUi.Output(ui, new { sandboxMode = mode, active, fallbackReason = runtime?.SandboxFallbackReason }, () =>
            TerminalOutput.Write(new Panel(new Text(L.Pick($"选择：{mode}\n当前：{active}\n{behavior}\n/sandbox auto | hyperlight | monty\n选择保存在当前工作区，下次启动沿用。", $"Selected: {mode}\nCurrent: {active}\n{behavior}\n/sandbox auto | hyperlight | monty\nThe choice is saved for the current workspace and reused on the next launch."))).Header(L.Pick("沙箱", "Sandbox"))));
    }

    private static void ReportSandbox(AgentRuntime runtime, TerminalUi ui)
    {
        ManagementUi.Output(ui, new { backend = runtime.SandboxName, fallbackReason = runtime.SandboxFallbackReason }, () =>
            TerminalOutput.Write(new Text(runtime.SandboxFallbackReason == null
                ? L.Pick("沙箱：", "Sandbox: ") + runtime.SandboxName + "\n"
                : L.Pick("Hyperlight 不可用，已自动切换到 ", "Hyperlight is unavailable; switched to ") + runtime.SandboxName + L.Pick("。\n原因：", ".\nReason: ") + runtime.SandboxFallbackReason + "\n")));
    }
    private static async ValueTask<bool> ConfirmAsync(string message, CancellationToken ct) => FullScreenTerminal.Current is { } screen ? await screen.ConfirmAsync(message, ct) : await AnsiConsole.PromptAsync(new ConfirmationPrompt(Markup.Escape(message)) { DefaultValue = false }, ct);
    internal static ApiConfig ResolveSubAgentConfig(ApiConfig current, IEnumerable<ApiConfig> configured, string? model, string? provider, string? defaultModel, string? defaultProvider)
    {
        var explicitProvider = !string.IsNullOrWhiteSpace(provider);
        return SubAgentModelSelection.Resolve(current, configured,
            !string.IsNullOrWhiteSpace(model) ? model : explicitProvider ? null : defaultModel,
            explicitProvider ? provider : defaultProvider);
    }

    private static async Task SessionsAsync(TerminalArguments args, DataStore store, TerminalUi ui)
    {
        var action = args.Positionals.FirstOrDefault();
        if (action == "export") { await ManagementUi.WriteNewJsonAsync(args.Get("output") ?? throw new ArgumentException(L.Pick("需要 --output。", "--output is required.")), SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, L.Pick("会话 ID", "conversation ID")), ui.Workspace)); ManagementUi.Success(ui, L.Pick("会话已导出。", "Conversation exported.")); }
        else if (action == "rename") { var c = SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, L.Pick("会话 ID", "conversation ID")), ui.Workspace); c.Title = ManagementUi.Require(args.Positionals, 2, L.Pick("标题", "title")); store.UpdateConversation(c); ManagementUi.Success(ui, L.Pick("会话已重命名。", "Conversation renamed.")); }
        else if (action == "branch") { var c = SessionOperations.Branch(store, SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, L.Pick("会话 ID", "conversation ID")), ui.Workspace), args.Positionals.ElementAtOrDefault(2)); ManagementUi.Output(ui, new { c.Id, c.Title }, () => TerminalOutput.Write(new Text(c.Id + "\n"))); }
        else SessionOperations.List(store, ui, action);
    }
    private static void ShowTools(IReadOnlyList<AgentToolDescriptor> tools, TerminalUi ui, ToolSurface? surface = null) => ManagementUi.Output(ui, tools, () =>
    {
        var table = new Table().AddColumn(L.Pick("工具", "Tool")).AddColumn(L.Pick("权限", "Permission")).AddColumn(L.Pick("披露", "Disclosure")).AddColumn(L.Pick("说明", "Description"));
        foreach (var tool in tools)
        {
            // On-demand families stay registered but out of the model catalog until their skill loads.
            var capability = surface?.Match(tool.Name);
            var disclosure = capability == null ? "always" : (surface!.IsAvailable(tool.Name) ? L.Pick("已加载 ", "loaded ") + capability.Label : L.Pick("按需 ", "on demand ") + capability.Label);
            table.AddRow(Markup.Escape(tool.Name), tool.Risk.ToString(), Markup.Escape(disclosure), Markup.Escape(tool.Description));
        }
        TerminalOutput.Write(table);
    });
    private static void ChatHelp(SkillCatalog catalog, bool all = false)
    {
        var table = new Table().Title(L.Pick("[bold cyan]AllAgent 命令面板[/]", "[bold cyan]AllAgent command panel[/]")).AddColumn(L.Pick("命令", "Command")).AddColumn(L.Pick("用途", "Purpose"));
        TerminalOutput.Write(new Text(L.Pick("直接输入你想完成的任务并回车。Agent 会使用工具，并可自行派发子代理；无需切换 Agent 模式。\n第一次使用：F2 选择模型，或 /login 登录。Esc 停止当前任务；空输入 Ctrl+C 两次或 Ctrl+D、/exit 退出。\n", "Type the task you want and press Enter. The agent uses tools and can dispatch child agents; there is no agent mode to switch.\nFirst time: F2 selects a model, or use /login. Esc stops the current task; empty input Ctrl+C twice or Ctrl+D, /exit quits.\n")));
        foreach (var c in all ? CommandCatalog.Builtins : CommandCatalog.Common) table.AddRow(Markup.Escape(c.Text + (c.Usage.Length > 0 ? " " + c.Usage : "")), Markup.Escape(c.Description));
        foreach (var s in catalog.List().Where(s => s.Enabled && s.UserInvocable)) table.AddRow(Markup.Escape("/" + s.Name + L.Pick(" 任务", " task")), Markup.Escape(s.Description));
        TerminalOutput.Write(table); TerminalOutput.Write(new Text(L.Pick("输入 / 查看常用操作；键入命令前缀查看子命令。/help all 或 Ctrl+P 搜索全部操作。\nTab 补全 · Ctrl+J 换行 · @ 引用文件 · Ctrl+R 搜索输入历史 · Ctrl+C/X/V 复制/剪切/粘贴\n", "Type / for common actions; type a command prefix for subcommands. /help all or Ctrl+P searches everything.\nTab completes · Ctrl+J newline · @ references files · Ctrl+R searches input history · Ctrl+C/X/V copy/cut/paste\n")));
    }
    private static void Help() => TerminalOutput.Write(new Text(L.Pick("""
        AllAgent — Spectre.Console · CodeAct · Hyperlight
        allagent                              开始聊天：直接描述任务，AI 自动使用工具和子代理
        allagent chat                         独立交互终端（多开共享历史，界面各自独立）
        allagent stats [--json]               按模型查看累计 Token 用量
        allagent run "任务" [--json]            单次任务 / 逐行 JSON 事件
        allagent providers [list|edit|refresh|models|show|enable|disable|remove]
        allagent skills [list|add|import|show|enable|disable|remove|export]
        allagent mcp [list|add|edit|connect|disconnect|tools|enable|disable|remove|import|export]
        allagent sessions [搜索词|export ID|rename ID 标题|branch ID]
        allagent plugins [list|search|install 包ID 版本|import 文件.nupkg|enable|disable|remove]
        allagent login [提供商ID|custom|插件ID|status]
        allagent logout [提供商ID]
        allagent hooks [list|enable 名称|disable 名称]
        allagent tasks [list|show ID]            任务跨启动保留结果；任务仅在当前 chat 进程内运行
        allagent powershell list|search 关键词|install 模块 [版本]|commands [模块]
        allagent doctor [--install]            环境检查；安装前询问
        allagent tools [--json]                工具契约
        allagent browser on|off|status        浏览器操作开关（默认开启）
        allagent browser connect             外置浏览器连接向导
        allagent browser tutorial            详细连接教程
        allagent browser mode builtin|chromium
        allagent web-ui                       浏览器工作台：/web-ui 启动、/web-ui status、/web-ui stop
        allagent web-ui --lan [--port 8765] [--password 密码]
                                              监听 0.0.0.0，生成二维码供其他设备访问；本机始终免密
        allagent web-ui --no-lan              关闭局域网监听，仅本机 127.0.0.1 访问（--local 同义）
        allagent -guimode                     原生窗口工作台：静默后台启动，关窗即退出
        allagent gui [--gui-url URL]          前台运行同一个窗口（Ctrl+C 退出；--gui-url 指向 Vite 开发服务器）
        allagent gui --gui-devtools           窗口内启用开发者工具
        allagent computer status              电脑操作状态（始终可用，技能按需加载）
        操作模式由 AI 根据任务自动选择；/computer-use 加载内置电脑操作技能
        allagent computer isolated URL Token文件路径（独立 Windows Worker 隧道）

        --workspace 路径 --data-dir 路径 --resume ID --config ID/名称
        --python 路径 --pwsh SDK-worker路径 --worker 路径 --guest 路径
        --model ID --effort 级别 --budget Token数 --verbose --json
        --subagent-model ID --subagent-provider 配置ID/名称（默认跟随当前模型）
        --context-window 100k|200k|400k|1M|Token数
        --full-access --restricted --ask --max-tools N --timeout 秒 --code-timeout 秒
        --background-concurrency N（默认 0：不限并发；正整数：主动限制后台并发）
        --allow-shell alias=绝对可执行文件路径（可重复；加入可信列表后免确认）

        login custom
          --model ID [--models ID1,ID2] [--key-env MY_API_KEY] [--reasoning-protocol openai]
        skills add --name 名称 --description 描述 --instructions-file 文件 [--scope project|user]
        agents [panel|edit 名称|add|list|show 名称|import 路径.json|remove 名称]
        mcp add --name 名称 --command 可执行文件或URL --args-json '["参数"]' [--transport stdio|http|sse]

        ALLAGENT_MODEL / ALLAGENT_API_KEY / ALLAGENT_BASE_URL / ALLAGENT_PROVIDER
        ALLAGENT_PROTOCOL / ALLAGENT_REASONING_PROTOCOL / ALLAGENT_EFFORT
        模型与扩展管理无需启动沙箱；首次任务才检查运行环境。
        默认限制模式（工作区内文件读写免审；工作区外路径、未信任程序与外部工具逐次授权）；--full-access 完全访问，--ask 逐次审批。
        """, """
        AllAgent — Spectre.Console · CodeAct · Hyperlight
        allagent                              Start chatting: describe a task; the AI uses tools and child agents
        allagent chat                         Standalone interactive terminal (instances share history, keep separate views)
        allagent stats [--json]               Cumulative token usage by model
        allagent run "task" [--json]          One-shot task / line-delimited JSON events
        allagent providers [list|edit|refresh|models|show|enable|disable|remove]
        allagent skills [list|add|import|show|enable|disable|remove|export]
        allagent mcp [list|add|edit|connect|disconnect|tools|enable|disable|remove|import|export]
        allagent sessions [query|export ID|rename ID title|branch ID]
        allagent plugins [list|search|install packageId version|import file.nupkg|enable|disable|remove]
        allagent login [providerId|custom|pluginId|status]
        allagent logout [providerId]
        allagent hooks [list|enable name|disable name]
        allagent tasks [list|show ID]            Results survive restarts; tasks run only inside the current chat process
        allagent powershell list|search query|install module [version]|commands [module]
        allagent doctor [--install]            Environment check; asks before installing
        allagent tools [--json]                Tool contracts
        allagent browser on|off|status        Browser-use switch (enabled by default)
        allagent browser connect             External-browser connection guide
        allagent browser tutorial            Detailed connection tutorial
        allagent browser mode builtin|chromium
        allagent web-ui                       Browser workspace: /web-ui start, /web-ui status, /web-ui stop
        allagent web-ui --lan [--port 8765] [--password password]
                                              Listen on 0.0.0.0 and show a QR code for other devices; this machine stays password-free
        allagent web-ui --no-lan              Disable LAN listening and use 127.0.0.1 only (same as --local)
        allagent -guimode                     Native window workspace: silent background launch, closes with the window
        allagent gui [--gui-url URL]          Run the same window in the foreground (Ctrl+C exits; --gui-url points at Vite)
        allagent gui --gui-devtools           Enable developer tools in the window
        allagent computer status              Computer-use status (always available; skills load on demand)
        The AI selects the operating mode per task; /computer-use loads the built-in computer-use skill
        allagent computer isolated URL tokenFile (isolated Windows Worker tunnel)

        --workspace path --data-dir path --resume ID --config ID/name
        --python path --pwsh SDK-worker-path --worker path --guest path
        --model ID --effort level --budget tokens --verbose --json
        --subagent-model ID --subagent-provider configId/name (follows the current model by default)
        --context-window 100k|200k|400k|1M|tokens
        --full-access --restricted --ask --max-tools N --timeout seconds --code-timeout seconds
        --background-concurrency N (default 0: unlimited; positive: cap background concurrency)
        --allow-shell alias=absolute-executable-path (repeatable; trusted entries skip confirmation)

        login custom
          --model ID [--models ID1,ID2] [--key-env MY_API_KEY] [--reasoning-protocol openai]
        skills add --name name --description text --instructions-file file [--scope project|user]
        agents [panel|edit name|add|list|show name|import path.json|remove name]
        mcp add --name name --command executable-or-URL --args-json '["args"]' [--transport stdio|http|sse]

        ALLAGENT_MODEL / ALLAGENT_API_KEY / ALLAGENT_BASE_URL / ALLAGENT_PROVIDER
        ALLAGENT_PROTOCOL / ALLAGENT_REASONING_PROTOCOL / ALLAGENT_EFFORT
        Model and extension management does not start a sandbox; the runtime is checked on the first task.
        Default is restricted (workspace reads/writes are free; outside paths, untrusted programs and external tools ask each time); --full-access allows everything, --ask asks for every call.
        """)));
}
