using LanAgent.Core.Tools;

namespace LanAgent.Core.Runtime;

public sealed record AgentOptions
{
    public ComputerUseState ComputerUse { get; init; } = new();
    public LanAgent.Core.Services.BackgroundTasks? BackgroundTasks { get; init; }
    public bool IsSubAgent { get; init; }
    public string? WebSearchBaseUrl { get; init; }
    public LanAgent.Core.Extensions.HookPipeline? Hooks { get; init; }
    public LanAgent.Core.Extensions.AgentExtensionPipeline? Extensions { get; init; }
    public LanAgent.Core.Services.SkillCatalog? Skills { get; init; }
    public LanAgent.Core.Services.WorkspaceCheckpoints? Checkpoints { get; init; }
    public Func<AgentQuestion, CancellationToken, Task<string?>>? QuestionHandler { get; init; }
    public Func<string>? InstructionsProvider { get; init; }
    /// <summary>Author profile is host metadata; children never update it. Disable for private sessions.</summary>
    public bool EnableMemory { get; init; } = true;
    public string AuthorProfileDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent");
    /// <summary>Live host mode check (e.g. plan mode) before automatic memory writes.</summary>
    public Func<bool>? MemoryUpdatesAllowed { get; init; }
    public TimeSpan AuthorProfileUpdateInterval { get; init; } = TimeSpan.FromMinutes(10);
    public int AuthorProfileBatchTurns { get; init; } = 3;
    public Func<IReadOnlyList<string>?>? HostToolsProvider { get; init; }
    /// <summary>Resolve a stable configuration snapshot at each model-step boundary.</summary>
    public Func<Guid, LanAgent.Core.Models.ApiConfig, LanAgent.Core.Models.ApiConfig>? ConfigurationProvider { get; init; }
    public string WorkspaceRoot { get; init; } = Environment.CurrentDirectory;
    public string? PythonPath { get; init; }
    public string? PowerShellPath { get; init; }
    public string SandboxWorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "sandbox", "LanAgent.Sandbox.exe");
    public string? PythonGuestPath { get; init; } = Environment.GetEnvironmentVariable("HYPERLIGHT_PYTHON_GUEST_PATH");
    /// <summary>Skip host tool approvals; Python always remains in Hyperlight.</summary>
    public bool FullAccess { get; init; } = true;
    public LanAgent.Core.Services.PythonPackageEnvironment? PythonLibraries { get; init; }
    public ExternalDirectoryPermissions ExternalDirectoryPermissions { get; init; } = new();
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
    public string? AuditPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent", "audit.jsonl");

    internal void Validate()
    {
        if (AuthorProfileUpdateInterval <= TimeSpan.Zero || AuthorProfileBatchTurns < 1 || MaxToolCalls < 1 || MaxModelRetries < 0 || MaxModelRetries > 20 || ModelRetryDelay < TimeSpan.Zero || MaxContextCharacters < 8000 ||
            RunTimeout != Timeout.InfiniteTimeSpan && RunTimeout <= TimeSpan.Zero || CodeTimeout <= TimeSpan.Zero || SandboxStartupTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid agent execution limits.");
        if (!Directory.Exists(WorkspaceRoot)) throw new DirectoryNotFoundException(WorkspaceRoot);
        if (Checkpoints != null && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot)), Checkpoints.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Checkpoint workspace differs from the agent workspace.");
    }
}
