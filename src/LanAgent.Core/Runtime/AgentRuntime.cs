using LanAgent.Core.CodeAct;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    public AgentOptions Options { get; }
    public RuntimeEnvironment Environment { get; }
    public ToolRegistry Tools { get; }
    public CodeActProvider CodeAct { get; }
    private readonly LanAgent.Core.Services.BackgroundTasks _jobs;

    private AgentRuntime(AgentOptions options, RuntimeEnvironment environment)
    {
        Options = options;
        Environment = environment;
        _jobs = options.BackgroundTasks ?? new();
        Tools = new ToolRegistry(options.FullAccess ? (request, _) => { if (request.Name is not ("file_write" or "file_edit" or "write" or "edit")) options.Checkpoints?.MarkExternalAction(); return ValueTask.FromResult(true); } : options.Approver, options.AuditPath, options.Hooks, options.InvocationPolicy, options.InvocationAuthorization);
        Tools.Register(new SystemTools(new WorkspacePolicy(options.WorkspaceRoot, options.FullAccess, options.ExternalDirectoryPermissions), environment, options.ShellAllowlist, options.Checkpoints, options.ExternalDirectoryPermissions, _jobs));
        if (!options.IsSubAgent) Tools.Register(new AgentInteractionTools(options.QuestionHandler));
        Tools.Register(new ComputerUseTools(options.ComputerUse));
        Tools.Register(new WebTools(options.WebSearchBaseUrl));
        if (options.PythonLibraries is { Ready: true } libraries) Tools.Register(new PythonLibraryTools(libraries));
        CodeAct = new CodeActProvider(Tools, new PythonCodeValidator(environment.Python!), new HyperlightProcessSandbox(options), options.ShellAllowlist, options.FullAccess,
            System.Text.Json.JsonSerializer.Serialize(new { workspaceRoot = Path.GetFullPath(options.WorkspaceRoot), python = environment.Python, powerShell = environment.PowerShell, operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fileReadPageCharacters = ToolResult.MaxOutputCharacters, sourceCharacters = 32000 }, ToolResult.JsonOptions),
            options.PythonLibraries is { Ready: true } contextLibraries ? () => File.ReadAllText(contextLibraries.CatalogPath) : null);
    }

    public static async Task<AgentRuntime> CreateAsync(AgentOptions options, CancellationToken ct = default)
    {
        options.Validate();
        if (options.PythonLibraries is { Ready: true } libraries) await libraries.RefreshAsync(ct);
        var doctor = new EnvironmentDoctor();
        var environment = options.InstallationApprover == null
            ? await doctor.CheckAsync(options.PythonPath, options.PowerShellPath, ct)
            : await doctor.EnsureAsync(options.InstallationApprover, options.PythonPath, options.PowerShellPath, ct);
        if (!environment.Ready) throw new InvalidOperationException(string.Join("\n", environment.Problems) + " 请运行 lanagent doctor --install。");
        var runtime = new AgentRuntime(options, environment);
        runtime.CodeAct.HostToolsProvider = options.HostToolsProvider;
        return runtime;
    }

    public async Task<ToolResult> ProbeSandboxAsync(CancellationToken ct = default) =>
        await CodeAct.ExecuteAsync("execute_code", System.Text.Json.JsonSerializer.SerializeToElement(new { code = "import base64, struct\ns = '中文 😀'\nassert base64.b64decode(base64.b64encode(s.encode('utf-16-le'))).decode('utf-16-le') == s\nassert struct.unpack('<H', struct.pack('<H', 1234))[0] == 1234\nprint(1 + 1)" }), CodeAct.CallToolAsync, ct);

    public async ValueTask DisposeAsync()
    {
        await CodeAct.DisposeAsync();
        if (Options.BackgroundTasks == null) await _jobs.DisposeAsync();
    }
}
