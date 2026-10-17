using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    public AgentOptions Options { get; }
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
        Environment = environment;
        PowerShellModules = new(options.SharedDirectory, environment.PowerShell);
        _jobs = options.BackgroundTasks ?? new();
        Tools = new ToolRegistry(options.FullAccess ? (request, _) => { if (request.Name is not ("file_write" or "file_edit" or "write" or "edit")) options.Checkpoints?.MarkExternalAction(); return ValueTask.FromResult(true); } : options.Approver, options.AuditPath, options.Hooks, options.InvocationPolicy, options.InvocationAuthorization, options.MaxToolResultCharacters, options.MaxToolStreamCharacters);
        Tools.Register(new SystemTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions), environment, options.ShellAllowlist, options.Checkpoints, options.ExternalDirectoryPermissions, _jobs, options.FileReadMaxBytes, options.FileReadMaxLines, PowerShellModules.Root));
        Tools.Register(new PowerShellModuleTools(PowerShellModules));
        Tools.Register(new PowerShellInvokeTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions), environment, PowerShellModules, options.Checkpoints, options.ExternalDirectoryPermissions));
        _sessions = new PowerShellSessions(environment.PowerShell!, PowerShellModules.Root, options.WorkspaceRoot);
        Tools.Register(new PowerShellScriptTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions), environment, _sessions, options.Checkpoints, options.ExternalDirectoryPermissions));
        if (!options.IsSubAgent) Tools.Register(new AgentInteractionTools(options.QuestionHandler));
        Tools.Register(new ComputerUseTools(options.ComputerUse));
        _browser = new BrowserUseTools(options.BrowserUse);
        Tools.Register(new BrowserSkillTools(_browser));
        if (options.Skills != null) Tools.Register(new SkillTools(options.Skills));
        Tools.Register(new WebTools(_browser));
        _sandbox = new FallbackCodeSandbox(options with { PythonPath = environment.Python });
        CodeAct = new CodeActProvider(Tools, new PythonCodeValidator(environment.Python!), _sandbox, options.ShellAllowlist, options.FullAccess,
            System.Text.Json.JsonSerializer.Serialize(new { workspaceRoot = Path.GetFullPath(options.WorkspaceRoot), python = environment.Python, shellRuntime = "Embedded PowerShell SDK; internal worker, not a pwsh CLI. Use powershell_script for PowerShell code in a persistent session; use powershell_invoke for one verified module command; use run_shell for external programs or delivered .ps1 files.", operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fileReadMaxBytes = options.FileReadMaxBytes, fileReadMaxLines = options.FileReadMaxLines, toolResultMaxCharacters = options.MaxToolResultCharacters, guestStdoutMaxCharacters = ToolResult.MaxOutputCharacters, sourceCharacters = options.MaxCodeCharacters }, ToolResult.JsonOptions),
            null, options.MaxCodeCharacters);
    }

    public static async Task<AgentRuntime> CreateAsync(AgentOptions options, CancellationToken ct = default)
    {
        options.Validate();
        var doctor = new EnvironmentDoctor();
        var environment = options.InstallationApprover == null
            ? await doctor.CheckAsync(options.PythonPath, options.PowerShellPath, ct)
            : await doctor.EnsureAsync(options.InstallationApprover, options.PythonPath, options.PowerShellPath, ct);
        if (!environment.Ready) throw new InvalidOperationException(string.Join("\n", environment.Problems) + " 请运行 allagent doctor --install。");
        var runtime = new AgentRuntime(options, environment);
        try { await runtime.PowerShellModules.ExecuteAsync("list", ct: ct); }
        catch { await runtime.DisposeAsync(); throw; }
        runtime.CodeAct.PowerShellCapabilitiesProvider = () => runtime.PowerShellModules.Summary;
        runtime.CodeAct.HostToolsProvider = options.HostToolsProvider;
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
        await CodeAct.DisposeAsync();
        await _browser.DisposeAsync();
        await _sessions.DisposeAsync();
        if (Options.BackgroundTasks == null) await _jobs.DisposeAsync();
    }
}
