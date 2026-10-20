using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (RuntimeEnvironment Environment, DateTime Expires)> EnvironmentCache = new();
    private bool _disposed;
    public AgentOptions Options { get; private set; }
    public RuntimeEnvironment Environment { get; }
    public ToolRegistry Tools { get; }
    public CodeActProvider CodeAct { get; }
    public AllAgent.NET.Core.Services.PowerShellModuleStore PowerShellModules { get; }
    private readonly AllAgent.NET.Core.Services.BackgroundTasks _jobs;
    private readonly FallbackCodeSandbox _sandbox;
    private readonly BrowserUseTools _browser;
    private readonly PowerShellSessions _sessions;
    public string SandboxName => _sandbox.Name;
    public string? SandboxFallbackReason => _sandbox.FallbackReason;

    private AgentRuntime(AgentOptions options, RuntimeEnvironment environment)
    {
        Options = options;
        // Retained workers keep their state while per-turn policies and callbacks refresh.
        options = options with { PermissionProvider = () => Options.CurrentPermission,
            TaskContractProvider = () => Options.TaskContractProvider?.Invoke(),
            InvocationPolicy = (tool, args) => Options.InvocationPolicy?.Invoke(tool, args),
            InvocationAuthorization = (tool, args, ct) => Options.InvocationAuthorization?.Invoke(tool, args, ct) ?? ValueTask.FromResult<ToolResult?>(null),
            Approver = (request, ct) => Options.Approver?.Invoke(request, ct) ?? ValueTask.FromResult(request.Preapproved),
            QuestionHandler = (question, ct) => Options.QuestionHandler?.Invoke(question, ct) ?? Task.FromResult<string?>(null) };
        Environment = environment;
        PowerShellModules = new(options.SharedDirectory, environment.PowerShell);
        _jobs = options.BackgroundTasks ?? new();
        Tools = new ToolRegistry(BuildApprover(options), options.AuditPath, options.Hooks,
            (tool, args) => HostToolGateway.CheckAvailability(tool, Options.HostToolsProvider?.Invoke(), Options.ReadOnlyProvider?.Invoke() == true, Options.SimpleModeActive)
                ?? options.TaskContractProvider?.Invoke()?.ValidateInvocation(tool, args, options.WorkspaceRoot) ?? options.InvocationPolicy?.Invoke(tool, args),
            options.InvocationAuthorization, options.MaxToolResultCharacters, options.MaxToolStreamCharacters,
            () => Options.CurrentPermission + ":" + (Options.ReadOnlyProvider?.Invoke() == true) + ":" +
                System.Text.Json.JsonSerializer.Serialize(new { contract = Options.TaskContractProvider?.Invoke(), tools = Options.HostToolsProvider?.Invoke() }, ToolResult.JsonOptions));
        var liveFullAccess = options.PermissionProvider == null ? null : new Func<bool>(() => options.FullAccess);
        Tools.Register(new SystemTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions, liveFullAccess), environment, options.ShellAllowlist, options.Checkpoints, options.ExternalDirectoryPermissions, _jobs, options.FileReadMaxBytes, options.FileReadMaxLines, PowerShellModules.Root));
        Tools.Register(new PowerShellModuleTools(PowerShellModules));
        Tools.Register(new PowerShellInvokeTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions, liveFullAccess), environment, PowerShellModules, options.Checkpoints, options.ExternalDirectoryPermissions));
        _sessions = new PowerShellSessions(environment.PowerShell!, PowerShellModules.Root, options.WorkspaceRoot);
        Tools.Register(new PowerShellScriptTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions, liveFullAccess), environment, _sessions, options.Checkpoints, options.ExternalDirectoryPermissions));
        if (!options.IsSubAgent) Tools.Register(new AgentInteractionTools(options.QuestionHandler));
        // The catalog carries the plan contract; AgentEngine replaces this instance with one bound
        // to the run's DataStore so a proposal can be persisted with the conversation.
        Tools.Register(new PlanTools(null, options.PlanHandler, options.ReadOnlyProvider));
        Tools.Register(new ComputerUseTools(options.ComputerUse));
        _browser = new BrowserUseTools(options.BrowserUse);
        Tools.Register(new BrowserSkillTools(_browser));
        if (options.Skills != null) Tools.Register(new SkillTools(options.Skills));
        Tools.Register(new WebTools(_browser));
        _sandbox = new FallbackCodeSandbox(options with { PythonPath = environment.Python });
        var surface = options.ToolSurface ?? new ToolSurface();
        CodeAct = new CodeActProvider(Tools, new PythonCodeValidator(environment.Python!), _sandbox, options.ShellAllowlist, options.CurrentPermission,
            System.Text.Json.JsonSerializer.Serialize(new { workspaceRoot = Path.GetFullPath(options.WorkspaceRoot), python = environment.Python, shellRuntime = "Embedded PowerShell SDK; internal worker, not a pwsh CLI. Use powershell_script for PowerShell code in a persistent session; use powershell_invoke for one verified module command; use run_shell for external programs or delivered .ps1 files.", operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fileReadMaxBytes = options.FileReadMaxBytes, fileReadMaxLines = options.FileReadMaxLines, toolResultMaxCharacters = options.MaxToolResultCharacters, guestStdoutMaxCharacters = ToolResult.MaxOutputCharacters, sourceCharacters = options.MaxCodeCharacters }, ToolResult.JsonOptions),
            null, options.MaxCodeCharacters, surface, options.Skills);
        // Declare on-demand capabilities once the registry knows which tool families exist.
        surface.Sync(ToolSurface.Discover(options.Skills, Tools.Definitions));
    }

    /// <summary>Re-declare on-demand capabilities after a host registered plugin skills or tools.</summary>
    public void RefreshToolSurface() => CodeAct.Surface.Sync(ToolSurface.Discover(Options.Skills, Tools.Definitions));

    internal void RefreshOptions(AgentOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Options = options;
        CodeAct.HostToolsProvider = () => Options.HostToolsProvider?.Invoke();
        CodeAct.PermissionProvider = () => Options.CurrentPermission;
        CodeAct.ReadOnlyProvider = () => Options.ReadOnlyProvider?.Invoke() == true;
        CodeAct.SimpleModeProvider = () => Options.SimpleModeActive;
    }

    /// <summary>Full access answers every request itself; restricted mode answers workspace-contained
    /// operations and delegates the rest to the host approver; ask mode delegates everything. With a live
    /// permission source the mode is resolved per request, so switching mid-run takes effect on the next
    /// tool call; without one the runtime keeps the stance it was built with.</summary>
    private static ToolApprover? BuildApprover(AgentOptions options)
    {
        if (options.PermissionProvider == null)
        {
            if (options.Permission == PermissionMode.Full)
                return (request, _) => { MarkExternal(options, request); return ValueTask.FromResult(true); };
            if (options.Permission != PermissionMode.Restricted) return options.Approver;
            return async (request, ct) =>
            {
                if (RestrictedAllows(request)) { MarkExternal(options, request); return true; }
                return options.Approver != null && await options.Approver(request, ct);
            };
        }
        return async (request, ct) =>
        {
            var permission = options.CurrentPermission;
            if (permission == PermissionMode.Full) { MarkExternal(options, request); return true; }
            if (permission == PermissionMode.Restricted && RestrictedAllows(request)) { MarkExternal(options, request); return true; }
            // A host without an approver keeps today's contract: ask mode runs preapproved calls only,
            // restricted mode denies anything the workspace shortcut did not already answer.
            if (options.Approver == null) return permission != PermissionMode.Restricted && request.Preapproved;
            return await options.Approver(request, ct);
        };
    }

    /// <summary>Restricted mode runs file operations whose target the host already authorized for this
    /// call and trusted shell aliases without asking. External directories, untrusted programs, MCP and
    /// desktop tools keep the host question; without an approver they are denied.</summary>
    private static bool RestrictedAllows(ToolApprovalRequest request) =>
        !request.RequiresExplicitApproval && request.Name != "external_directory" &&
        (request.WorkspaceScoped || request.Preapproved);

    /// <summary>A checkpointed file write can be reverted; anything else may have effects outside the workspace.</summary>
    private static void MarkExternal(AgentOptions options, ToolApprovalRequest request)
    {
        if (request.Name is not ("file_write" or "file_edit" or "write" or "edit")) options.Checkpoints?.MarkExternalAction();
    }

    public static async Task<AgentRuntime> CreateAsync(AgentOptions options, CancellationToken ct = default)
    {
        options.Validate();
        var doctor = new EnvironmentDoctor();
        var environmentKey = (options.PythonPath ?? System.Environment.GetEnvironmentVariable("PATH")) + "|" + (options.PowerShellPath ?? AppContext.BaseDirectory);
        var environment = EnvironmentCache.TryGetValue(environmentKey, out var cached) && cached.Expires > DateTime.UtcNow && File.Exists(cached.Environment.Python) && File.Exists(cached.Environment.PowerShell)
            ? cached.Environment : options.InstallationApprover == null
            ? await doctor.CheckAsync(options.PythonPath, options.PowerShellPath, ct)
            : await doctor.EnsureAsync(options.InstallationApprover, options.PythonPath, options.PowerShellPath, ct);
        if (!environment.Ready) throw new InvalidOperationException(string.Join("\n", environment.Problems) + " 请运行 allagent doctor --install。");
        EnvironmentCache[environmentKey] = (environment, DateTime.UtcNow.AddMinutes(5));
        var runtime = new AgentRuntime(options, environment);
        try { await runtime.PowerShellModules.ExecuteAsync("list", ct: ct); }
        catch { await runtime.DisposeAsync(); throw; }
        runtime.CodeAct.PowerShellCapabilitiesProvider = () => runtime.PowerShellModules.Summary;
        runtime.RefreshOptions(options);
        runtime.CodeAct.IsSubAgent = options.IsSubAgent;
        return runtime;
    }

    public async Task<ToolResult> ProbeSandboxAsync(CancellationToken ct = default)
    {
        try { await CodeAct.PrepareAsync(ct); }
        catch (InvalidOperationException ex) { return ToolResult.Failure(ex.Message); }
        return await CodeAct.ExecuteAsync("execute_code", System.Text.Json.JsonSerializer.SerializeToElement(new { code = "import json\nassert json.loads('{\"value\": 2}')[\"value\"] == 2\nprint(1 + 1)" }), CodeAct.CallToolAsync, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CodeAct.DisposeAsync();
        await _browser.DisposeAsync();
        await _sessions.DisposeAsync();
        if (Options.BackgroundTasks == null) await _jobs.DisposeAsync();
    }

    /// <summary>Metadata-only construction for listings; does not probe/install or start workers.</summary>
    public static AgentRuntime CreateCatalog(AgentOptions options) => new(options,
        new(options.PythonPath, options.PowerShellPath ?? Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.PowerShell.exe"), []));
}
