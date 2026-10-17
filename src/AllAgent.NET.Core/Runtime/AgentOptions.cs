using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

public enum SandboxMode { Auto, Hyperlight, Monty }

public sealed record AgentOptions
{
    public SandboxMode SandboxMode { get; init; } = SandboxMode.Auto;
    public ComputerUseState ComputerUse { get; init; } = new();
    public BrowserUseState BrowserUse { get; init; } = new();
    public AllAgent.NET.Core.Services.BackgroundTasks? BackgroundTasks { get; init; }
    public bool IsSubAgent { get; init; }
    public string? WebSearchBaseUrl { get; init; }
    public AllAgent.NET.Core.Extensions.HookPipeline? Hooks { get; init; }
    public AllAgent.NET.Core.Extensions.AgentExtensionPipeline? Extensions { get; init; }
    public AllAgent.NET.Core.Services.SkillCatalog? Skills { get; init; }
    public AllAgent.NET.Core.Services.WorkspaceCheckpoints? Checkpoints { get; init; }
    public Func<AgentQuestion, CancellationToken, Task<string?>>? QuestionHandler { get; init; }
    public Func<string>? InstructionsProvider { get; init; }
    /// <summary>Volatile instruction tail (preferences, learned policies) kept out of stable prompt cache breakpoints.</summary>
    public Func<string>? VolatileInstructionsProvider { get; init; }
    public string SharedDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET");
    public Func<IReadOnlyList<string>?>? HostToolsProvider { get; init; }
    /// <summary>Resolve a stable configuration snapshot at each model-step boundary.</summary>
    public Func<Guid, AllAgent.NET.Core.Models.ApiConfig, AllAgent.NET.Core.Models.ApiConfig>? ConfigurationProvider { get; init; }
    public string WorkspaceRoot { get; init; } = Environment.CurrentDirectory;
    public string? PythonPath { get; init; }
    /// <summary>Optional path to the bundled AllAgent.NET.PowerShell SDK worker, not system pwsh.</summary>
    public string? PowerShellPath { get; init; }
    /// <summary>Allow provisioning the pinned Monty fallback when Hyperlight cannot start.</summary>
    public bool AllowSandboxInstallation { get; init; } = true;
    public string SandboxWorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "sandbox", "AllAgent.NET.Sandbox.exe");
    public string? PythonGuestPath { get; init; } = Environment.GetEnvironmentVariable("HYPERLIGHT_PYTHON_GUEST_PATH");
    /// <summary>Skip host tool approvals; Python always remains in the selected sandbox.</summary>
    public bool FullAccess { get; init; } = true;
    public AllAgent.NET.Core.Services.PythonPackageEnvironment? PythonLibraries { get; init; }
    public ExternalDirectoryPermissions ExternalDirectoryPermissions { get; init; } = new();
    public int FileReadMaxBytes { get; init; } = 20_000;
    public int FileReadMaxLines { get; init; } = 2_000;
    public int MaxCodeCharacters { get; init; } = 32_000;
    public int MaxToolResultCharacters { get; init; } = 64_000;
    public int MaxToolStreamCharacters { get; init; } = 20_000;
    /// <summary>Opt in to replaying a request after output or usage has arrived; replay may incur additional charges.</summary>
    public bool RetryAfterModelOutput { get; init; }
    public int MaxToolCalls { get; init; } = int.MaxValue;
    public int MaxModelRetries { get; init; } = 5;
    public TimeSpan ModelRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxContextCharacters { get; init; } = 40000000;
    public TimeSpan RunTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    private TimeSpan? _codeTimeout;
    public TimeSpan CodeTimeout { get => _codeTimeout ?? TimeSpan.FromSeconds(FullAccess ? 600 : 60); init => _codeTimeout = value; }
    public TimeSpan SandboxStartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyDictionary<string, string> ShellAllowlist { get; init; } = new Dictionary<string, string>();
    public ToolApprover? Approver { get; init; }
    public Func<AgentToolDescriptor, System.Text.Json.JsonElement, ToolResult?>? InvocationPolicy { get; init; }
    /// <summary>Additional live authorization, including read tools; full access does not bypass it.</summary>
    public Func<AgentToolDescriptor, System.Text.Json.JsonElement, CancellationToken, ValueTask<ToolResult?>>? InvocationAuthorization { get; init; }
    public Func<string, CancellationToken, ValueTask<bool>>? InstallationApprover { get; init; }
    public string? AuditPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET", "audit.jsonl");

    internal void Validate()
    {
        if (!Enum.IsDefined(SandboxMode)) throw new ArgumentException("Invalid sandbox mode.");
        if (FileReadMaxBytes < 256 || FileReadMaxBytes > 1_000_000 || FileReadMaxLines < 1 ||
            MaxCodeCharacters < 1 || MaxCodeCharacters > 128_000 || MaxToolResultCharacters < 4096 || MaxToolResultCharacters > 4_000_000 ||
            MaxToolStreamCharacters < 1 || MaxToolStreamCharacters > MaxToolResultCharacters)
            throw new ArgumentException("Invalid tool output limits.");
        if (MaxToolCalls < 1 || MaxModelRetries < 0 || MaxModelRetries > 20 || ModelRetryDelay < TimeSpan.Zero || MaxContextCharacters < 8000 ||
            RunTimeout != Timeout.InfiniteTimeSpan && RunTimeout <= TimeSpan.Zero || CodeTimeout <= TimeSpan.Zero || SandboxStartupTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid agent execution limits.");
        if (!Directory.Exists(WorkspaceRoot)) throw new DirectoryNotFoundException(WorkspaceRoot);
        if (Checkpoints != null && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot)), Checkpoints.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Checkpoint workspace differs from the agent workspace.");
    }
}
