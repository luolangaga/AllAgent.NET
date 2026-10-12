using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using LanAgent.Core.Extensions;
using System.Runtime.CompilerServices;
using Spectre.Console;

namespace LanAgent.Terminal;

internal static class TerminalApp
{
    internal const int MaxProjectInstructionCharacters = 100000;
    public static async Task<int> RunAsync(string[] raw)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8; Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        TerminalUi? ui = null;
        try
        {
            var args = TerminalArguments.Parse(raw); ui = new(args.Flags.Contains("json"), args.Flags.Contains("verbose"));
            if (args.Flags.Contains("help") || args.Command == "help") { Help(); return 0; }
            var approval = new ToolsApproval(ui) { FullAccess = args.Flags.Contains("full-access") }; var options = args.Options(approval);
            ui.Workspace = options.WorkspaceRoot;
            if (args.Command == "doctor") return await DoctorAsync(args, options, ui);
            var dataDir = Path.GetFullPath(args.Get("data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent"));
            if (args.Command == "tasks")
            {
                if (args.Positionals.FirstOrDefault() is not (null or "list" or "show")) throw new ArgumentException("独立进程仅支持 tasks list/show；授权、回答和取消请在运行任务的聊天界面使用 /tasks。");
                await using var snapshot = new BackgroundTasks(dataDir, readOnly: true); await new TaskManager(snapshot, ui).ExecuteAsync(args.Positionals.Count == 0 ? ["list"] : args.Positionals); return 0;
            }
            using var dataLease = new DataDirectoryLease(dataDir);
            using var store = new DataStore(dataDir);
            var roles = new AgentRoleCatalog(store);
            LoadingIndicator.Set(store.GetSetting("terminal:spinner"));
            BackgroundTasks? roleTasks = null;
            var roleManager = new AgentRoleManager(roles, ui, name => roleTasks?.List.Any(job =>
                job.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval && store.GetConversation(job.SessionId)?.AgentRole == name) == true);
            if (args.Command == "agents") { await roleManager.ExecuteAsync(args.Positionals); return 0; }
            ui.Inbox = new InputInbox(store, options.WorkspaceRoot);
            if (args.Command == "stats") { UsageStatistics.Show(store, ui); return 0; }
            await using var plugins = new PluginHost(dataDir);
            plugins.ReserveAuthenticationIds("codex");
            await using var tasks = new BackgroundTasks(dataDir, args.BackgroundConcurrency);
            roleTasks = tasks;
            var extensions = new ExtensionManager(plugins, tasks, dataDir, ui, args, store);
            var taskManager = new TaskManager(tasks, ui, store);
            var catalog = new SkillCatalog(store, options.WorkspaceRoot, dataDir);
            var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(options.WorkspaceRoot), dataDir);
            checkpoints.RecoverRevert(store);
            var checkpointManager = new CheckpointManager(checkpoints, store, ui);
            approval.Checkpoints = checkpoints;
            options = options with { BackgroundTasks = tasks, Checkpoints = checkpoints, QuestionHandler = ui.RequestQuestionAsync, Hooks = plugins.Hooks };
            catalog.SeedBundled();
            var models = new ModelManager(store, ui, args, () => plugins.Providers, extensions.FindAuthentication, () => extensions.Authentication);
            await using var mcp = new McpToolService(store, new SkillExecutor());
            mcp.AuthenticationResolver = extensions.FindAuthentication;
            plugins.ProvideService(store); plugins.ProvideService(catalog); plugins.ProvideService(tasks); plugins.ProvideService(mcp);
            await plugins.LoadEnabledAsync();
            var mcpManager = new McpManager(store, mcp, ui, args); var skills = new SkillManager(catalog, ui, args);
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
                case "tasks": await taskManager.ExecuteAsync(args.Positionals); return 0;
                case "tools":
                    var env = await new EnvironmentDoctor().CheckAsync(options.PythonPath, options.PowerShellPath);
                    ShowTools(new SystemTools(new WorkspacePolicy(options.WorkspaceRoot), env, options.ShellAllowlist).Definitions
                        .Concat(new WebTools(options.WebSearchBaseUrl).Definitions)
                        .Concat(new AgentInteractionTools(options.QuestionHandler).Definitions)
                        .Concat(new SubAgentTools(tasks, _ => throw new InvalidOperationException("Tool listing does not execute agents.")).Definitions)
                        .Concat(plugins.ToolSet().Definitions).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(), ui); return 0;
                case "run": case "chat": break;
                default: throw new ArgumentException("未知命令；使用 --help。");
            }
            if (args.Command == "chat" && !ui.Interactive) throw new ArgumentException("交互聊天需要终端；自动化使用 run --json。");
            if (args.Command == "chat" && ui.Json) throw new ArgumentException("JSON 事件模式请使用 run --json；chat 使用交互界面。");
            if (args.Command == "run" && args.Positionals.Count == 0) throw new ArgumentException("请提供任务，例如 lanagent run \"列出当前目录\"。");
            ui.Verbose = ui.Verbose || store.GetSetting("terminal:verbose") == "on";
            ui.ShowThinking = store.GetSetting("terminal:thinking") != "off";
            var conversation = SessionOperations.Get(store, args.Get("resume")); ApiConfig? config = null; AgentRuntime? runtime = null; var connectedDefaults = false;
            if (args.Get("config") == null && args.Get("model") == null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANAGENT_MODEL")))
            {
                var saved = store.ApiConfigs.FirstOrDefault(c => c.IsEnabled && c.Id.ToString() == store.GetSetting("terminal:lastConfig"));
                if (saved != null) config = await models.SelectAsync(saved.Id.ToString());
            }
            string? runtimeRole = null;
            var navigationModels = new Dictionary<Guid, ApiConfig>();
            options = options with { InstructionsProvider = () => Instructions(options.WorkspaceRoot, catalog, approval.PlanMode) + "\nAvailable child agents:\n" + string.Join("\n", roles.List().Select(role => role.Name + ": " + role.Description)) };
            IAiProvider Provider(ApiConfig selected)
            {
                var provider = new AiProviderFactory(plugins.Providers).GetProvider(selected);
                return selected.AuthenticationProviderId is { Length: > 0 } id ? new AuthenticatedAiProvider(provider, extensions.FindAuthentication(id)) : provider;
            }
            async Task<string> StartDelegatedAsync(SubAgentRequest request, CancellationToken ct)
            {
                var expanded = await PromptReferences.ExpandWithImagesAsync(request.Prompt, options.WorkspaceRoot, ct);
                ct.ThrowIfCancellationRequested();
                return StartJob(request.Prompt, model: request.Model, provider: request.Provider, request: request, expanded: expanded);
            }
            string StartJob(string prompt, string kind = "子 Agent", string? model = null, string? provider = null, SubAgentRequest? request = null, PromptReferences.ExpandedPrompt? expanded = null)
            {
                if (AgentExecutionScope.IsSubAgent) throw new InvalidOperationException("子 Agent 不能再次派发任务。");
                request ??= new("general", kind + " · " + ToolResult.Clip(prompt, 40), prompt, model, provider);
                var roleDefinition = roles.Get(request.Agent);
                var roleChanged = request.SessionId == null || store.GetConversation(request.SessionId.Value)?.AgentRole != request.Agent;
                var effectiveModel = model ?? (roleChanged ? roleDefinition.Model : null);
                var effectiveProvider = provider ?? (roleChanged ? roleDefinition.Provider : null);
                var selected = ResolveSubAgentConfig(config ?? throw new InvalidOperationException("请先选择模型。"), store.ApiConfigs, effectiveModel, effectiveProvider,
                    args.Get("subagent-model") ?? store.GetSetting("subagents:model"), args.Get("subagent-provider") ?? store.GetSetting("subagents:provider"));
                var child = SubAgentSessions.Resolve(store, tasks, conversation.Id, request, allowRunning: true, roles: roles);
                if (request.SessionId != null && effectiveProvider == null && child.SubAgentConfigId is { } savedConfig)
                    selected = SubAgentModelSelection.Resolve(selected, store.ApiConfigs, effectiveModel ?? child.ModelId, savedConfig.ToString());
                if (request.SessionId != null)
                {
                    var joined = tasks.TryJoin(child.Id, conversation.Id, _ =>
                    {
                        if (child.Revert != null) throw new InvalidOperationException("子会话处于暂存撤回状态，不能追加指令。");
                        store.EnqueueInput(child.Id, request.Prompt, expanded?.Text ?? request.Prompt, expanded?.Images ?? [], "steer", selected, request.Agent);
                    });
                    if (joined != null) return joined;
                }
                child.SubAgentConfigId = selected.Id; child.ModelId = selected.DefaultModel; child.ProviderId = selected.ProviderId;
                if (request.SessionId == null) store.AddConversation(child); else store.UpdateConversation(child);
                var planning = approval.PlanMode || conversation.ParentConversationId != null && roles.Get(conversation.AgentRole ?? "general").ReadOnly;
                var childFullAccess = approval.FullAccess && !planning;
                var firstRun = true;
                return tasks.Start(prompt, child.Id, kind, (jobId, ct) =>
                {
                    var fromInbox = !firstRun; firstRun = false;
                    return RunJobAsync(jobId, child.Id, prompt, selected, planning, childFullAccess, ct, fromInbox, expanded);
                }, conversation.Id, () => store.GetPendingInputs(child.Id).Any(input => input.Delivery == "steer"));
            }
            plugins.ProvideService(new AgentTaskLauncher(task => StartJob(task, "插件任务")));
            async IAsyncEnumerable<AgentEvent> RunJobAsync(string jobId, Guid session, string prompt, ApiConfig selected, bool planning, bool fullAccess, [EnumeratorCancellation] CancellationToken ct, bool fromInbox = false, PromptReferences.ExpandedPrompt? snapshot = null)
            {
                AgentRoleDefinition CurrentRole() => roles.Get(store.GetConversation(session)?.AgentRole ?? "general");
                bool ReadOnly() => planning || CurrentRole().ReadOnly;
                var rolePolicy = new SubAgentRolePolicy(ReadOnly, options.WorkspaceRoot, options.ExternalDirectoryPermissions, CurrentRole,
                    async (request, token) => ui.Interactive && !ui.Json && await tasks.ApproveAsync(jobId, request, token));
                var childOptions = options with
                {
                    IsSubAgent = true,
                    FullAccess = fullAccess,
                    InvocationPolicy = (tool, arguments) => rolePolicy.Validate(tool, arguments) ?? options.InvocationPolicy?.Invoke(tool, arguments),
                    InvocationAuthorization = async (tool, arguments, token) => (options.InvocationAuthorization == null ? null : await options.InvocationAuthorization(tool, arguments, token)) ?? await rolePolicy.AuthorizeAsync(tool, arguments, token),
                    ConfigurationProvider = (childId, current) =>
                    {
                        var state = store.GetConversation(childId) ?? throw new InvalidOperationException("子会话已不存在。");
                        return SubAgentModelSelection.Resolve(current, store.ApiConfigs, state.ModelId, state.SubAgentConfigId?.ToString());
                    },
                    Checkpoints = null, InstallationApprover = null,
                    InstructionsProvider = () => Instructions(options.WorkspaceRoot, catalog, ReadOnly()) + "\nYou are a delegated agent. Stay within the assigned task. You cannot spawn agents. Follow the selected host permission mode; do not bypass a denial." +
                        "\nCurrent agent role: " + CurrentRole().Name + "\n" + CurrentRole().Instructions + (ReadOnly() ? "\nHost restriction: read-only. Do not create, modify, delete, move or copy files, or execute external operations." : ""),
                    Approver = async (request, token) => ReadOnly() ? false : request.Preapproved ? true : !ui.Interactive || ui.Json ? false : await tasks.ApproveAsync(jobId, request, token),
                    QuestionHandler = (question, token) => ui.Interactive && !ui.Json ? tasks.AskAsync(jobId, question, token) : Task.FromResult<string?>(null)
                };
                await using var childRuntime = await AgentRuntime.CreateAsync(childOptions, ct);
                childRuntime.Tools.Register(new SkillTools(catalog)); childRuntime.Tools.Register(new McpResourceTools(mcp)); childRuntime.Tools.Register(new McpToolSet(mcp)); childRuntime.Tools.Register(plugins.ToolSet());
                var engine = new AgentEngine(store, new RequestRoutingAiProvider(Provider), childRuntime.CodeAct, childOptions);
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
                if (tasks.List.Any(j => j.SessionId == conversation.Id && j.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval))
                    throw new InvalidOperationException("此子会话仍在运行；请通过 /tasks 处理授权、回答或取消后再继续输入。");
                var role = conversation.ParentConversationId == null ? "primary" : conversation.AgentRole ?? "general";
                var resumedRole = role == "primary" ? null : roles.Get(role);
                var resumedPolicy = resumedRole == null ? null : new SubAgentRolePolicy(() => approval.PlanMode || roles.Get(role).ReadOnly,
                    options.WorkspaceRoot, options.ExternalDirectoryPermissions, () => roles.Get(role), approval.ApproveAsync);
                var executionOptions = options with
                {
                    IsSubAgent = role != "primary",
                    FullAccess = options.FullAccess,
                    InvocationPolicy = resumedPolicy == null ? options.InvocationPolicy : (tool, arguments) => resumedPolicy.Validate(tool, arguments) ?? options.InvocationPolicy?.Invoke(tool, arguments),
                    InvocationAuthorization = resumedPolicy == null ? options.InvocationAuthorization : async (tool, arguments, token) => (options.InvocationAuthorization == null ? null : await options.InvocationAuthorization(tool, arguments, token)) ?? await resumedPolicy.AuthorizeAsync(tool, arguments, token),
                    Approver = resumedRole == null ? options.Approver : (request, token) => approval.PlanMode || roles.Get(role).ReadOnly ? ValueTask.FromResult(false) : options.Approver?.Invoke(request, token) ?? ValueTask.FromResult(request.Preapproved),
                    InstructionsProvider = () => (options.InstructionsProvider?.Invoke() ?? "") + (resumedRole == null ? "" : "\n" + roles.Get(role).Instructions)
                };
                if (runtime != null && runtimeRole != role) { await runtime.DisposeAsync(); runtime = null; }
                if (config == null || !config.IsEnabled) config = await models.SelectAsync(args.Get("config"), args.Get("model"));
                if (runtime == null)
                {
                    var doctor = new EnvironmentDoctor();
                    var environment = await ManagementUi.Busy("检查 Python 和 PowerShell 环境", ct => options.FullAccess ? doctor.EnsureAsync((_, _) => ValueTask.FromResult(true), options.PythonPath, options.PowerShellPath, ct) : ui.Interactive && !ui.Json ? doctor.EnsureAsync(ConfirmAsync, options.PythonPath, options.PowerShellPath, ct) : doctor.CheckAsync(options.PythonPath, options.PowerShellPath, ct));
                    if (!environment.Ready) throw new InvalidOperationException(string.Join("\n", environment.Problems) + " 使用 doctor --install。");
                    options = options with { PythonPath = environment.Python, PowerShellPath = environment.PowerShell };
                    if (!ui.Json) TerminalOutput.MarkupLine("[dim]正在检查 Hyperlight 沙箱…[/]");
                    executionOptions = executionOptions with { PythonPath = options.PythonPath, PowerShellPath = options.PowerShellPath };
                    runtime = await ManagementUi.Busy("启动 Hyperlight 沙箱", ct => AgentRuntime.CreateAsync(executionOptions, ct)); runtimeRole = role; var probe = await ManagementUi.Busy("验证 Hyperlight 沙箱", ct => runtime.ProbeSandboxAsync(ct));
                    if (!probe.Ok) { await runtime.DisposeAsync(); runtime = null; throw new InvalidOperationException(probe.Error + "\n请检查 Windows Hypervisor Platform、BIOS 虚拟化及 sandbox 原生组件。"); }
                    runtime.Tools.Register(new SkillTools(catalog));
                    runtime.Tools.Register(new McpResourceTools(mcp));
                    runtime.Tools.Register(new SubAgentTools(tasks, (task, model, provider) => StartJob(task, model: model, provider: provider),
                        launchAsync: StartDelegatedAsync));
                }
                if (!connectedDefaults)
                {
                    await CancelableAsync(ct => ManagementUi.Busy("连接 MCP 服务", token => mcp.ConnectAllEnabledAsync(token), ct)); connectedDefaults = true;
                    foreach (var c in store.McpServerConfigs.Where(c => c.IsEnabled && !mcp.IsConnected(c.Id))) ui.Error("MCP " + c.Name + "：" + mcp.GetError(c.Id));
                }
                runtime.Tools.Replace<McpToolSet>(new McpToolSet(mcp));
                runtime.Tools.Replace<PluginToolSet>((PluginToolSet)plugins.ToolSet());
                if (FullScreenTerminal.Current != null) ui.Header(options.WorkspaceRoot, config.DefaultModel, conversation.Id);
                return new AgentEngine(store, Provider(config), runtime.CodeAct, executionOptions);
            }
            void Navigate(Conversation target)
            {
                if (config != null) navigationModels[conversation.Id] = ManagementUi.Clone(config);
                conversation = target;
                if (navigationModels.TryGetValue(target.Id, out var remembered)) config = ManagementUi.Clone(remembered);
                else if (target.SubAgentConfigId is { } saved && store.ApiConfigs.Any(c => c.Id == saved && c.IsEnabled))
                    config = SubAgentModelSelection.Resolve(config ?? store.ApiConfigs.First(c => c.Id == saved), store.ApiConfigs, target.ModelId, saved.ToString());
                ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? target.ModelId, target.Id);
            }
            try
            {
                if (args.Command == "run")
                {
                    var engine = await EngineAsync(); if (!ui.Json) ui.Header(options.WorkspaceRoot, config!.DefaultModel, conversation.Id);
                    await RunTurnAsync(engine, conversation.Id, string.Join(" ", args.Positionals), config!, ui, checkpoints, store);
                    return ui.LastReason is "stop" or "STOP" ? 0 : ui.LastReason == "cancelled" ? 130 : 1;
                }
                using var screen = !args.Flags.Contains("inline") && AnsiConsole.Profile.Capabilities.Ansi ? new FullScreenTerminal(store, catalog, mcp, ui) : null;
                screen?.Enter();
                if (screen != null) screen.ExtensionSummary = () => $"插件 · {plugins.Status.Count(p => p.Enabled && p.Error == null)} 个\n任务 · {tasks.List.Count(j => j.State is BackgroundTaskState.Running or BackgroundTaskState.Queued)} 运行 / 排队\n等待处理 · {tasks.List.Count(j => j.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval)} 个  /tasks";
                if (screen != null) screen.TaskNotice = () => (tasks.List.Count(j => j.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval) is var count && count > 0 ? $"后台待处理 {count} · /tasks | " : "") + (ui.Inbox?.Label is { Length: > 0 } inboxLabel ? inboxLabel + " | " : "");
                if (screen != null) screen.TaskLookup = tasks.Get;
                if (screen != null)
                {
                    var taskViews = new Dictionary<string, TaskTranscriptView>();
                    screen.TaskTranscript = task =>
                    {
                        if (!taskViews.TryGetValue(task.Id, out var view)) taskViews[task.Id] = view = new(store, tasks, task.Id, () => ui.ShowThinking);
                        return view;
                    };
                }
                ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? "尚未选择模型 · 管理功能可直接使用", conversation.Id);
                if (conversation.Revert is { } stagedRevert)
                    ui.Draft = await SessionTimeline.RestoreDraftAsync(store.GetMessages(conversation.Id).Single(message => message.Id == stagedRevert.MessageId));
                var editor = new InputEditor(Path.Combine(dataDir, "terminal-history.json"), options.WorkspaceRoot, () => CommandCatalog.Builtins.Concat(plugins.Commands.Select(c => new CommandHint("/ext " + c.Name, c.Description))).Concat(catalog.List().Where(s => s.Enabled && s.UserInvocable).Select(s => new CommandHint("/" + s.Name, s.Description, "任务参数", IsSkill: true))));
                editor.WakeRequested = () => conversation.Revert == null && tasks.PendingNotifications(conversation.Id).Count > 0;
                while (true)
                {
                    if (ui.Inbox?.Next is { } pendingInput)
                    {
                        try
                        {
                            var pendingEngine = await EngineAsync();
                            await RunTurnAsync(pendingEngine, conversation.Id, pendingInput.Content, config!, ui, checkpoints, store, pendingInput: pendingInput);
                        }
                        catch (OperationCanceledException) { ui.Error("待处理输入执行已取消。"); }
                        catch (Exception ex) { ui.Error(ex.Message); }
                        if (ui.Inbox.Pending.Any(item => item.Id == pendingInput.Id))
                        {
                            ui.Inbox.AutomaticDeliveryPaused = true;
                            ui.Error("待处理输入已保留；修复启动问题后 /queue run 重试，或 /queue edit|delete ID。");
                        }
                        continue;
                    }
                    if (conversation.Revert == null && tasks.PendingNotifications(conversation.Id).Count > 0)
                    {
                        var results = SubAgentNotifications.Deliver(store, tasks, conversation.Id);
                        if (results.Count > 0)
                        {
                            try
                            {
                                ManagementUi.Success(ui, "收到子代理完成结果 · 正在继续父任务。");
                                var resumedEngine = await EngineAsync();
                                await RunTurnAsync(resumedEngine, conversation.Id, "Review the newly delivered delegated task results and continue the existing parent objective. Verify claims before relying on them.", config!, ui, checkpoints, store, internalMessage: true);
                            }
                            catch (OperationCanceledException) { ui.Error("父任务继续处理已取消；子代理结果已保存。"); }
                            catch (Exception ex) { ui.Error(ex.Message); }
                        }
                        continue;
                    }
                    var text = await editor.ReadAsync($"{config?.DisplayName ?? "未选择提供商"} · {config?.DefaultModel ?? "按需配置"} · 思考 {config?.ReasoningEffort ?? "default"} · {approval.ModeLabel} · 会话 {conversation.Id.ToString()[..8]} · {ui.Inbox?.Label}", initial: ui.Draft);
                    if (editor.InterruptedDraft != null) { ui.Draft = editor.InterruptedDraft; editor.InterruptedDraft = null; }
                    else if (screen == null) ui.Draft = "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    try
                    {
                        PromptReferences.ExpandedPrompt? preparedPrompt = null;
                        if (text.StartsWith('/'))
                        {
                            var originalCommand = text;
                            var words = CommandLineWords.Parse(text); var command = words[0]; var rest = words.Skip(1).ToArray(); var tail = text.Length > command.Length ? text[(command.Length + 1)..].Trim() : "";
                            switch (command)
                            {
                                case "/exit": case "/quit": return 0;
                                case "/help": ChatHelp(catalog, rest.FirstOrDefault() == "all"); continue;
                                case "/stats": UsageStatistics.Show(store, ui); continue;
                                case "/timeline":
                                    if (rest.FirstOrDefault() == "list" || rest.Length == 0 && screen == null)
                                    {
                                        var rows = new Table().AddColumn("消息 ID").AddColumn("时间").AddColumn("用户输入");
                                        foreach (var message in SessionTimeline.Entries(store, conversation.Id)) rows.AddRow(message.Id.ToString()[..8], message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"), Markup.Escape(SessionTimeline.Text(message)));
                                        TerminalOutput.Write(rows); continue;
                                    }
                                    var timelineChoice = rest.Length == 0 ? await screen!.TimelineAsync() :
                                        new FullScreenTerminal.TimelineChoice(SessionTimeline.Find(store, conversation.Id, ManagementUi.Require(rest, 1, "消息 ID")).Id, rest[0]);
                                    if (timelineChoice == null) continue;
                                    var timelineMessage = SessionTimeline.Find(store, conversation.Id, timelineChoice.MessageId.ToString());
                                    switch (timelineChoice.Action)
                                    {
                                        case "jump": if (screen == null) throw new ArgumentException("消息定位需要全屏终端。"); screen.JumpToMessage(timelineMessage.Id); break;
                                        case "copy": if (!ClipboardText.Write(SessionTimeline.Text(timelineMessage))) throw new IOException("剪贴板写入失败。"); ManagementUi.Success(ui, "消息正文已复制。"); break;
                                        case "restore": ui.Draft = await SessionTimeline.RestoreDraftAsync(timelineMessage); break;
                                        case "revert":
                                            if (tasks.List.Any(job => job.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingApproval or BackgroundTaskState.AwaitingAnswer)) throw new InvalidOperationException("请先等待或停止后台任务，再撤回文件和历史。");
                                            await checkpointManager.StageAsync(conversation, timelineMessage.Id); break;
                                        case "fork":
                                            var forkDraft = await SessionTimeline.RestoreDraftAsync(timelineMessage);
                                            var fork = SessionOperations.Branch(store, conversation, null, timelineMessage.Id);
                                            Navigate(fork); ui.Draft = forkDraft; break;
                                        default: throw new ArgumentException("用法：/timeline [list|jump|copy|restore|fork ID]");
                                    }
                                    continue;
                                case "/queue":
                                    ui.Draft = await ui.Inbox!.CommandAsync(text, ui.Draft) ?? ui.Draft; continue;
                                case "/steer":
                                    if (string.IsNullOrWhiteSpace(tail)) throw new ArgumentException("用法：/steer 任务说明");
                                    await ui.Inbox!.EnqueueAsync(tail, "steer"); continue;
                                case "/plugins": await CancelableAsync(ct => extensions.PluginsAsync(rest, ct)); runtime?.Tools.Replace<PluginToolSet>((PluginToolSet)plugins.ToolSet()); continue;
                                case "/hooks": extensions.Hooks(rest); continue;
                                case "/login": await CancelableAsync(ct => extensions.LoginAsync(rest, ct)); if (rest.FirstOrDefault() is not ("status" or "list")) config = null; continue;
                                case "/logout": await CancelableAsync(ct => extensions.LogoutAsync(rest, ct)); config = null; continue;
                                case "/tasks": await taskManager.ExecuteAsync(rest); continue;
                                case "/agents": await roleManager.ExecuteAsync(rest); continue;
                                case "/background":
                                    if (string.IsNullOrWhiteSpace(tail)) throw new ArgumentException("用法：" + command + " 任务描述。");
                                    await EngineAsync(); var jobId = StartJob(tail, "后台任务"); tasks.NotifyParentOnCompletion(jobId); ManagementUi.Success(ui, "已启动 " + jobId[..8] + "；完成后自动通知父会话，使用 /tasks 管理。"); continue;
                                case "/subagents":
                                    if (rest.FirstOrDefault() == "inherit")
                                    {
                                        args.Values.Remove("subagent-model"); args.Values.Remove("subagent-provider");
                                        store.SetSetting("subagents:model", ""); store.SetSetting("subagents:provider", "");
                                        ManagementUi.Success(ui, "子代理默认跟随当前模型。");
                                    }
                                    else if (rest.Length > 0)
                                    {
                                        config ??= await models.SelectAsync(args.Get("config"), args.Get("model"));
                                        var childModel = SubAgentModelSelection.Resolve(config, store.ApiConfigs, rest.ElementAtOrDefault(1), rest[0]);
                                        args.Values.Remove("subagent-model"); args.Values.Remove("subagent-provider");
                                        store.SetSetting("subagents:provider", childModel.Id.ToString());
                                        store.SetSetting("subagents:model", rest.ElementAtOrDefault(1) ?? "");
                                        ManagementUi.Success(ui, "子代理默认模型：" + childModel.DisplayName + " / " + childModel.DefaultModel);
                                    }
                                    else
                                    {
                                        var childProvider = args.Get("subagent-provider") ?? store.GetSetting("subagents:provider");
                                        var childDefault = args.Get("subagent-model") ?? store.GetSetting("subagents:model");
                                        childProvider = store.ApiConfigs.FirstOrDefault(c => c.Id.ToString() == childProvider)?.DisplayName ?? childProvider;
                                        TerminalOutput.Write(new Text("子代理默认：" + (string.IsNullOrWhiteSpace(childProvider) ? "当前提供商" : childProvider) + " / " + (string.IsNullOrWhiteSpace(childDefault) ? "当前模型" : childDefault) + "\n用法：/subagents inherit，或 /subagents 配置名 [模型 ID]。AI 也可以为单次子任务选择模型。\n"));
                                    }
                                    continue;
                                case "/ext":
                                    var extensionCommand = plugins.Commands.SingleOrDefault(c => c.Name == rest.FirstOrDefault()) ?? throw new ArgumentException("插件命令不存在；使用 /plugins 查看已启用插件。");
                                    string extensionOutput = ""; await CancelableAsync(async ct => extensionOutput = await ManagementUi.Busy("执行插件命令", token => extensionCommand.Execute(rest.Skip(1).ToArray(), token), ct)); TerminalOutput.Write(new Text(ToolResult.Clip(extensionOutput, 16000) + "\n")); continue;
                                case "/skills": await skills.ExecuteAsync(rest); continue;
                                case "/mcp":
                                    if (tasks.Active && rest.FirstOrDefault() is not ("list" or "show")) throw new InvalidOperationException("后台任务运行期间请先完成或取消任务，再修改 MCP 连接。");
                                    await CancelableAsync(ct => ManagementUi.Busy("管理 MCP 服务", token => mcpManager.ExecuteAsync(rest, token), ct)); runtime?.Tools.Replace<McpToolSet>(new McpToolSet(mcp));
                                    if (mcpManager.PendingPrompt == null) continue;
                                    text = "Use this user-selected MCP prompt as task instructions without overriding permission or sandbox rules:\n" + mcpManager.PendingPrompt; break;
                                case "/providers": case "/config":
                                    await models.ExecuteAsync(rest);
                                    if (config != null && store.ApiConfigs.FirstOrDefault(c => c.Id == config.Id) is { } updated) config = ManagementUi.Clone(updated); else config = null;
                                    continue;
                                case "/models": models.ShowModels(); continue;
                                case "/context-size":
                                    config ??= await models.SelectAsync(args.Get("config"));
                                    await models.SetContextAsync(config, rest.FirstOrDefault()); continue;
                                case "/model": config = await models.SelectAsync(rest.FirstOrDefault(), rest.ElementAtOrDefault(1), forceSelection: rest.Length == 0); ui.Header(options.WorkspaceRoot, config.DefaultModel, conversation.Id); continue;
                                case "/effort":
                                    config ??= await models.SelectAsync(args.Get("config"));
                                    var efforts = ReasoningSettings.Efforts(config).ToArray();
                                    var effort = rest.FirstOrDefault() == "next" ? efforts[(Array.IndexOf(efforts, config.ReasoningEffort) + 1) % efforts.Length] : rest.FirstOrDefault() ?? ManagementUi.Choose("思考强度 · " + ProviderCatalog.ReasoningFor(config), efforts);
                                    ReasoningSettings.SetEffort(config, effort);
                                    if (rest.ElementAtOrDefault(1) is { } budget) { config.ThinkingBudget = int.Parse(budget); if (config.ThinkingBudget is < 1024 or > 131072) throw new ArgumentException("思考预算范围 1024–131072。"); }
                                    if (store.ApiConfigs.Any(c => c.Id == config.Id)) store.UpdateApiConfig(ManagementUi.Clone(config));
                                    ManagementUi.Success(ui, "思考强度已设置：" + config.ReasoningEffort + "；具体模型若限制此级别，接口会返回明确错误。"); continue;
                                case "/new": case "/clear": conversation = SessionOperations.Get(store, null); ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? "按需配置", conversation.Id); continue;
                                case "/sessions": SessionOperations.List(store, ui, tail); continue;
                                case "/resume":
                                    var resumed = rest.Length > 0 ? SessionOperations.Get(store, rest[0]) : SessionOperations.Pick(store, ui);
                                    Navigate(resumed); continue;
                                case "/parent": Navigate(SessionOperations.Parent(store, conversation)); continue;
                                case "/children": Navigate(SessionOperations.Child(store, conversation, rest.FirstOrDefault(), ui)); continue;
                                case "/copy":
                                    var copyText = rest.FirstOrDefault() switch
                                    {
                                        null => SessionTranscript.Format(conversation),
                                        "last" => SessionTranscript.LastReply(store.GetVisibleMessages(conversation.Id)),
                                        "id" => conversation.Id.ToString(),
                                        _ => throw new ArgumentException("用法：/copy [last|id]。")
                                    };
                                    if (!ClipboardText.Write(copyText)) throw new IOException("剪贴板写入失败，请重试或使用 /export。");
                                    ManagementUi.Success(ui, "已复制到剪贴板。"); continue;
                                case "/rename": conversation.Title = ManagementUi.Require(rest, 0, "标题") + (rest.Length > 1 ? " " + string.Join(' ', rest.Skip(1)) : ""); store.UpdateConversation(conversation); ManagementUi.Success(ui, "会话已重命名。"); continue;
                                case "/fork":
                                    Guid? forkBefore = null; string? forkName = null;
                                    if (rest.FirstOrDefault() == "--before")
                                    {
                                        forkBefore = SessionTimeline.Find(store, conversation.Id, ManagementUi.Require(rest, 1, "消息 ID")).Id;
                                        if (rest.Length > 2) forkName = string.Join(' ', rest.Skip(2));
                                    }
                                    else if (rest.Length == 0)
                                    {
                                        var forkChoices = new[] { new SessionTimeline.ForkChoice(null, "完整会话") }.Concat(SessionTimeline.Entries(store, conversation.Id)
                                            .Select(message => new SessionTimeline.ForkChoice(message.Id, message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + SessionTimeline.Text(message).Replace('\n', ' '))));
                                        forkBefore = ManagementUi.Choose("会话分支 · 选择历史边界", forkChoices, item => item.Label).MessageId;
                                    }
                                    else forkName = tail;
                                    var preparedForkDraft = forkBefore is { } before ? await SessionTimeline.RestoreDraftAsync(SessionTimeline.Find(store, conversation.Id, before.ToString())) : null;
                                    Navigate(SessionOperations.Branch(store, conversation, forkName, forkBefore));
                                    if (preparedForkDraft != null) ui.Draft = preparedForkDraft; continue;
                                case "/branch": conversation = SessionOperations.Branch(store, conversation, tail.Length == 0 ? null : tail); ui.Header(options.WorkspaceRoot, config?.DefaultModel ?? conversation.ModelId, conversation.Id); continue;
                                case "/history": SessionOperations.History(store, conversation.Id); continue;
                                case "/checkpoints": case "/diff": case "/undo": case "/rewind": case "/redo":
                                    if (command is "/undo" or "/rewind" or "/redo" && tasks.List.Any(job => job.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingApproval or BackgroundTaskState.AwaitingAnswer)) throw new InvalidOperationException("请先等待或停止后台任务，再还原文件和历史。");
                                    await checkpointManager.ExecuteAsync(command, conversation, rest.FirstOrDefault()); continue;
                                case "/export":
                                    var exportPath = rest.FirstOrDefault(r => !r.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "session-" + Guid.NewGuid().ToString("N") + ".md");
                                    if (rest.Any(r => r.StartsWith("--") && r is not ("--no-thinking" or "--no-tools"))) throw new ArgumentException("导出选项：--no-thinking / --no-tools。");
                                    await SessionTranscript.ExportAsync(conversation, exportPath, !rest.Contains("--no-thinking"), !rest.Contains("--no-tools")); ManagementUi.Success(ui, "会话已导出：" + Path.GetFullPath(exportPath)); continue;
                                case "/context": SessionOperations.Context(conversation, options.MaxContextCharacters); continue;
                                case "/compact": config ??= await models.SelectAsync(args.Get("config")); await CancelableAsync(ct => SessionOperations.CompactAsync(store, conversation, config, ui, options.MaxContextCharacters, tail, ct, Provider(config))); continue;
                                case "/plan":
                                    if (tasks.Active) throw new InvalidOperationException("请先完成或取消后台任务，再切换权限。");
                                    approval.PlanMode = !approval.PlanMode;
                                    options = options with { FullAccess = approval.FullAccess && !approval.PlanMode };
                                    if (runtime != null) { await runtime.DisposeAsync(); runtime = null; }
                                    ManagementUi.Success(ui, "权限模式：" + approval.ModeLabel); continue;
                                case "/permissions":
                                    if (rest.FirstOrDefault() == "directories")
                                    {
                                        if (rest.ElementAtOrDefault(1) == "clear") options.ExternalDirectoryPermissions.Clear(conversation.Id);
                                        var directories = options.ExternalDirectoryPermissions.List(conversation.Id);
                                        TerminalOutput.Write(new Panel(new Text(directories.Count == 0 ? "此会话未记住外部目录。" : string.Join('\n', directories))).Header("外部目录权限 · 此会话")); continue;
                                    }
                                    var mode = rest.FirstOrDefault();
                                    if (mode == null && ui.Interactive) mode = ManagementUi.Choose("权限模式", new[] { "ask", "full", "plan" }, value => value switch { "full" => "完全访问 · 不逐次审核 · 可访问工作区外文件", "plan" => "只读规划", _ => "逐次授权" });
                                    if (mode != null)
                                    {
                                        if (mode is not ("ask" or "full" or "plan")) throw new ArgumentException("权限模式：ask / full / plan。");
                                        if (tasks.Active) throw new InvalidOperationException("请先完成或取消后台任务，再切换权限。");
                                        approval.PlanMode = mode == "plan"; approval.FullAccess = mode == "full";
                                        options = options with { FullAccess = approval.FullAccess };
                                        if (runtime != null) { await runtime.DisposeAsync(); runtime = null; }
                                    }
                                    TerminalOutput.Write(new Panel(new Text(approval.ModeLabel + "\n" + (approval.PlanMode ? "文件读取可用；写入、Shell 和 MCP 操作停用。" : approval.FullAccess ? "文件、Shell、MCP 与插件工具不再逐次审核；允许工作区外的文件和已安装可执行程序。\n双击 Esc / Ctrl+C 仍可停止任务。" : "文件写入、未信任的 Shell 程序和 MCP 操作执行前显示参数并询问；可信 Shell 别名免逐次确认。"))).Header("当前权限")); continue;
                                case "/tools": if (runtime == null) await EngineAsync(); ShowTools(runtime!.Tools.Definitions, ui); continue;
                                case "/status":
                                    TerminalOutput.Write(new Panel(new Text($"工作区：{options.WorkspaceRoot}\n会话：{conversation.Id}\n模型：{config?.DisplayName ?? "未选择"} / {config?.DefaultModel}\n思考：{config?.ReasoningEffort ?? "default"}\n权限：{approval.ModeLabel}\n沙箱：{(runtime != null ? "Hyperlight 已验证" : "首次任务时检查")}\n技能：{catalog.List().Count(s => s.Enabled)}\nMCP：{mcp.GetAllServerTools().Count} 个已连接\n审计：{options.AuditPath}")).Header("会话状态")); continue;
                                case "/doctor": await DoctorAsync(args, options, ui); continue;
                                case "/settings":
                                    if (rest.Length == 0)
                                    {
                                        var setting = ManagementUi.Choose("显示与主题设置", "工具详情", "思考内容", "界面主题", "加载指示", "返回聊天");
                                        if (setting == "返回聊天") continue;
                                        if (setting == "加载指示")
                                        {
                                            LoadingIndicator.Set(ManagementUi.Choose("加载指示样式 · 当前 " + LoadingIndicator.Name, LoadingIndicator.Names, name => name));
                                            store.SetSetting("terminal:spinner", LoadingIndicator.Name);
                                            ManagementUi.Success(ui, "加载指示已保存：" + LoadingIndicator.Name); continue;
                                        }
                                        if (setting == "思考内容")
                                        {
                                            ui.ShowThinking = ManagementUi.Choose("显示思考内容", "关闭", "开启") == "开启";
                                            store.SetSetting("terminal:thinking", ui.ShowThinking ? "on" : "off"); continue;
                                        }
                                        if (setting == "界面主题")
                                        {
                                            var selectedTheme = ManagementUi.Choose("界面主题", TerminalTheme.All, t => t.Name);
                                            store.SetSetting("terminal:theme", selectedTheme.Id); if (screen != null) screen.Theme = selectedTheme; continue;
                                        }
                                        ui.Verbose = ManagementUi.Choose("显示工具详情", "关闭", "开启") == "开启";
                                    }
                                    else if (rest.Length == 2 && rest[0] == "verbose" && rest[1] is "on" or "off") ui.Verbose = rest[1] == "on";
                                    else throw new ArgumentException("用法 /settings verbose on|off。");
                                    store.SetSetting("terminal:verbose", ui.Verbose ? "on" : "off"); ManagementUi.Success(ui, "终端设置已保存。"); continue;
                                case "/themes":
                                    var theme = rest.FirstOrDefault() is { } themeId ? TerminalTheme.All.Single(t => t.Id == themeId) : ManagementUi.Choose("界面主题", TerminalTheme.All, t => t.Name);
                                    store.SetSetting("terminal:theme", theme.Id); if (screen != null) screen.Theme = theme; ManagementUi.Success(ui, "主题已保存：" + theme.Name); continue;
                                case "/spinner":
                                    if (rest.FirstOrDefault() == "list") { TerminalOutput.Write(new Text(string.Join(" · ", LoadingIndicator.Names))); continue; }
                                    var spinner = rest.FirstOrDefault() ?? ManagementUi.Choose("加载指示样式 · 当前 " + LoadingIndicator.Name, LoadingIndicator.Names, name => name);
                                    LoadingIndicator.Set(spinner); store.SetSetting("terminal:spinner", LoadingIndicator.Name); ManagementUi.Success(ui, "加载指示已保存：" + LoadingIndicator.Name); continue;
                                case "/thinking": ui.ShowThinking = !ui.ShowThinking; store.SetSetting("terminal:thinking", ui.ShowThinking ? "on" : "off"); ManagementUi.Success(ui, ui.ShowThinking ? "显示模型提供的思考内容。" : "隐藏思考内容；任务状态仍显示。"); continue;
                                case "/details": ui.Verbose = !ui.Verbose; store.SetSetting("terminal:verbose", ui.Verbose ? "on" : "off"); ManagementUi.Success(ui, ui.Verbose ? "工具详情已开启。" : "工具详情已关闭。"); continue;
                                case "/memory": Memory(options.WorkspaceRoot, rest, ui); continue;
                                case "/init": Init(options.WorkspaceRoot, ui); continue;
                                default:
                                    if (catalog.List().Any(s => s.Enabled && s.UserInvocable && "/" + s.Name == command))
                                    {
                                        await CancelableAsync(async ct => preparedPrompt = await PromptReferences.ExpandSkillAsync(catalog, command[1..], tail, options.WorkspaceRoot, ct));
                                        text = preparedPrompt!.Text;
                                    }
                                    else { ui.Error("未知命令；输入 / 查看提示面板，或 /help。"); continue; }
                                    break;
                            }
                            screen?.UserMessage(originalCommand);
                        }
                        var engine = await EngineAsync(); await RunTurnAsync(engine, conversation.Id, text, config!, ui, checkpoints, store, preparedPrompt: preparedPrompt);
                    }
                    catch (OperationCanceledException) { TerminalOutput.MarkupLine("[dim]操作已取消；可以继续输入。[/]"); }
                    catch (Exception ex) { ui.Error(ex.Message); }
                }
            }
            finally { await tasks.DisposeAsync(); if (runtime != null) await runtime.DisposeAsync(); }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) { if (ui != null) ui.Error(ex.Message); else Console.Error.WriteLine(ex.Message); return 1; }
    }
    internal static string Instructions(string workspace, SkillCatalog catalog, bool plan)
    {
        var policy = new WorkspacePolicy(workspace); var parts = new List<string>();
        foreach (var relative in new[] { "AGENTS.md", ".lanagent/AGENT.md" })
        {
            var path = policy.Resolve(relative); if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > MaxProjectInstructionCharacters * 4L + 4) throw new ArgumentException(relative + " 超过项目指令大小上限（100000 字符）。");
            var content = File.ReadAllText(path);
            if (content.Length > MaxProjectInstructionCharacters) throw new ArgumentException(relative + " 超过项目指令大小上限（100000 字符）。");
            parts.Add("Project instructions from " + relative + ":\n" + content);
        }
        parts.Add(catalog.Instructions); if (plan) parts.Add("PLAN mode: only read and reason. Do not request writes, shell or MCP execution. Deliver a concrete plan for review."); return string.Join("\n\n", parts);
    }
    private static void Memory(string workspace, string[] args, TerminalUi ui)
    {
        var path = new WorkspacePolicy(workspace).Resolve(".lanagent/AGENT.md");
        if (args.FirstOrDefault() == "set") { var content = string.Join(' ', args.Skip(1)); if (content.Length is 0 or > MaxProjectInstructionCharacters) throw new ArgumentException("项目指令需要 1–100000 字符。"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); ManagementUi.Success(ui, "项目指令已保存，下次任务自动加载。"); }
        else TerminalOutput.Write(new Panel(new Text(File.Exists(path) ? File.ReadAllText(path) : "尚未设置；/init 创建模板，/memory set 内容 设置指令。工作区 AGENTS.md 也会自动加载。")).Header("项目指令 · " + Markup.Escape(path)));
    }
    private static void Init(string workspace, TerminalUi ui)
    {
        var path = new WorkspacePolicy(workspace).Resolve(".lanagent/AGENT.md"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new StreamWriter(new FileStream(path, FileMode.CreateNew)); file.Write("# Project instructions\n\nDescribe the project, build/test commands, coding conventions and delivery requirements here.\n"); ManagementUi.Success(ui, "项目指令模板已创建：" + path);
    }
    private static async Task RunTurnAsync(AgentEngine engine, Guid id, string text, ApiConfig config, TerminalUi ui, WorkspaceCheckpoints checkpoints, DataStore store, bool internalMessage = false, PendingInput? pendingInput = null, PromptReferences.ExpandedPrompt? preparedPrompt = null)
    {
        checkpoints.Begin(id, text, store.GetVisibleMessages(id));
        try { await CancelableAsync(async ct =>
        {
            var prompt = preparedPrompt ?? (pendingInput == null ? await PromptReferences.ExpandWithImagesAsync(text, ui.Workspace, ct, allowExplicitExternalImages: !internalMessage)
                : new PromptReferences.ExpandedPrompt(pendingInput.Content, pendingInput.Images));
            if (pendingInput == null && prompt.Images.Count > 0 && !ui.Json) TerminalOutput.Write(ImageAttachmentsView.Render(prompt.Images));
            await ui.RunAgentAsync(engine.RunAsync(id, prompt.Text, config, ct, internalMessage, prompt.Images, fromInbox: pendingInput != null, inputText: text), ct);
        }); }
        finally
        {
            checkpoints.End(store.GetVisibleMessages(id));
            if (ui.Inbox?.Session == id && store.GetConversation(id)?.Revert == null) ui.Inbox.AutomaticDeliveryPaused = false;
        }
    }
    internal static async Task CancelableAsync(Func<CancellationToken, Task> action)
    {
        using var source = new CancellationTokenSource(); ConsoleCancelEventHandler handler = (_, evt) => { evt.Cancel = true; source.Cancel(); }; Console.CancelKeyPress += handler;
        try { await action(source.Token); } finally { Console.CancelKeyPress -= handler; }
    }
    private static async Task<int> DoctorAsync(TerminalArguments args, AgentOptions options, TerminalUi ui)
    {
        var doctor = new EnvironmentDoctor(); if (args.Flags.Contains("install") && !ui.Interactive) throw new ArgumentException("安装需要交互终端逐项确认。");
        var environment = args.Flags.Contains("install") ? await doctor.EnsureAsync(ConfirmAsync, options.PythonPath, options.PowerShellPath) : await doctor.CheckAsync(options.PythonPath, options.PowerShellPath);
        ManagementUi.Output(ui, environment, () => { var table = new Table().AddColumn("环境").AddColumn("结果"); table.AddRow("Python 3.10–3.14", Markup.Escape(environment.Python ?? "缺失")); table.AddRow("PowerShell 7.6", Markup.Escape(environment.PowerShell ?? "缺失")); table.AddRow("工作区", Markup.Escape(options.WorkspaceRoot)); TerminalOutput.Write(table); foreach (var issue in environment.Problems) ui.Error(issue); });
        if (!environment.Ready) return 1;
        await using var runtime = await AgentRuntime.CreateAsync(options with { PythonPath = environment.Python, PowerShellPath = environment.PowerShell }); var probe = await runtime.ProbeSandboxAsync();
        ManagementUi.Output(ui, new { sandbox = probe }, () => { if (probe.Ok) TerminalOutput.MarkupLine("[green]✓ Hyperlight microVM 实际执行校验通过[/]"); else ui.Error(probe.Error ?? "沙箱失败"); }); return probe.Ok ? 0 : 1;
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
        if (action == "export") { await ManagementUi.WriteNewJsonAsync(args.Get("output") ?? throw new ArgumentException("需要 --output。"), SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, "会话 ID"))); ManagementUi.Success(ui, "会话已导出。"); }
        else if (action == "rename") { var c = SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, "会话 ID")); c.Title = ManagementUi.Require(args.Positionals, 2, "标题"); store.UpdateConversation(c); ManagementUi.Success(ui, "会话已重命名。"); }
        else if (action == "branch") { var c = SessionOperations.Branch(store, SessionOperations.Get(store, ManagementUi.Require(args.Positionals, 1, "会话 ID")), args.Positionals.ElementAtOrDefault(2)); ManagementUi.Output(ui, new { c.Id, c.Title }, () => TerminalOutput.Write(new Text(c.Id + "\n"))); }
        else SessionOperations.List(store, ui, action);
    }
    private static void ShowTools(IReadOnlyList<AgentToolDescriptor> tools, TerminalUi ui) => ManagementUi.Output(ui, tools, () => { var table = new Table().AddColumn("工具").AddColumn("权限").AddColumn("说明"); foreach (var t in tools) table.AddRow(Markup.Escape(t.Name), t.Risk.ToString(), Markup.Escape(t.Description)); TerminalOutput.Write(table); });
    private static void ChatHelp(SkillCatalog catalog, bool all = false)
    {
        var table = new Table().Title("[bold cyan]LanAgent 命令面板[/]").AddColumn("命令").AddColumn("用途");
        TerminalOutput.Write(new Text("直接输入你想完成的任务并回车。Agent 会使用工具，并可自行派发子代理；无需切换 Agent 模式。\n第一次使用：F2 选择模型，或 /login 登录。双击 Esc 停止当前任务。\n"));
        foreach (var c in all ? CommandCatalog.Builtins : CommandCatalog.Common) table.AddRow(Markup.Escape(c.Text + (c.Usage.Length > 0 ? " " + c.Usage : "")), Markup.Escape(c.Description));
        foreach (var s in catalog.List().Where(s => s.Enabled && s.UserInvocable)) table.AddRow(Markup.Escape("/" + s.Name + " 任务"), Markup.Escape(s.Description));
        TerminalOutput.Write(table); TerminalOutput.Write(new Text("输入 / 查看常用操作；键入命令前缀查看子命令。/help all 或 Ctrl+P 搜索全部操作。\nTab 补全 · Ctrl+J 换行 · @ 引用文件 · Ctrl+R 搜索输入历史 · Ctrl+C/X/V 复制/剪切/粘贴\n"));
    }
    private static void Help() => TerminalOutput.Write(new Text("""
        LanAgent — Spectre.Console · CodeAct · Hyperlight
        lanagent                              开始聊天：直接描述任务，AI 自动使用工具和子代理
        lanagent chat                         交互终端（/ 命令提示面板）
        lanagent stats [--json]               按模型查看累计 Token 用量
        lanagent run "任务" [--json]            单次任务 / 逐行 JSON 事件
        lanagent providers [list|add|edit|refresh|models|show|enable|disable|remove|presets]
        lanagent skills [list|add|import|show|enable|disable|remove|export]
        lanagent mcp [list|add|edit|connect|disconnect|tools|enable|disable|remove|import|export]
        lanagent sessions [搜索词|export ID|rename ID 标题|branch ID]
        lanagent plugins [list|search|install 包ID 版本|import 文件.nupkg|enable|disable|remove]
        lanagent login [codex|插件ID|status]
        lanagent logout [提供商ID]
        lanagent hooks [list|enable 名称|disable 名称]
        lanagent tasks [list|show ID]            任务跨启动保留结果；任务仅在当前 chat 进程内运行
        lanagent doctor [--install]            环境检查；安装前询问
        lanagent tools [--json]                工具契约

        --workspace 路径 --data-dir 路径 --resume ID --config ID/名称
        --python 路径 --pwsh 路径 --worker 路径 --guest 路径
        --model ID --effort 级别 --budget Token数 --verbose --json
        --subagent-model ID --subagent-provider 配置ID/名称（默认跟随当前模型）
        --context-window 100k|200k|400k|1M|Token数
        --full-access --max-tools N --timeout 秒 --code-timeout 秒
        --background-concurrency N（默认 0：不限并发；正整数：主动限制后台并发）
        --allow-shell alias=绝对可执行文件路径（可重复；加入可信列表后免确认）

        providers add --provider custom --name 名称 --base-url URL --protocol openai
          --model ID [--models ID1,ID2] [--key-env MY_API_KEY] [--reasoning-protocol openai]
        skills add --name 名称 --description 描述 --instructions-file 文件 [--scope project|user]
        agents list|show 名称|import 路径.json|remove 名称
        mcp add --name 名称 --command 可执行文件或URL --args-json '["参数"]' [--transport stdio|http|sse]

        LANAGENT_MODEL / LANAGENT_API_KEY / LANAGENT_BASE_URL / LANAGENT_PROVIDER
        LANAGENT_PROTOCOL / LANAGENT_REASONING_PROTOCOL / LANAGENT_EFFORT
        模型与扩展管理无需启动沙箱；首次任务才检查运行环境。
        普通非交互模式拒绝操作授权；--full-access 显式开启完全访问。
        """));
}
