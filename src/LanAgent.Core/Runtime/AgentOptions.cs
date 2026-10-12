using LanAgent.Core.Tools;

namespace LanAgent.Core.Runtime;

public sealed record AgentOptions
{
    public LanAgent.Core.Services.BackgroundTasks? BackgroundTasks { get; init; }
    public bool IsSubAgent { get; init; }
    public string? WebSearchBaseUrl { get; init; }
    public LanAgent.Core.Extensions.HookPipeline? Hooks { get; init; }
    public LanAgent.Core.Services.WorkspaceCheckpoints? Checkpoints { get; init; }
    public Func<AgentQuestion, CancellationToken, Task<string?>>? QuestionHandler { get; init; }
    public Func<string>? InstructionsProvider { get; init; }
    /// <summary>Resolve a stable configuration snapshot at each model-step boundary.</summary>
    public Func<Guid, LanAgent.Core.Models.ApiConfig, LanAgent.Core.Models.ApiConfig>? ConfigurationProvider { get; init; }
    public string WorkspaceRoot { get; init; } = Environment.CurrentDirectory;
    public string? PythonPath { get; init; }
    public string? PowerShellPath { get; init; }
    public string SandboxWorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "sandbox", "LanAgent.Sandbox.exe");
    public string? PythonGuestPath { get; init; } = Environment.GetEnvironmentVariable("HYPERLIGHT_PYTHON_GUEST_PATH");
    public bool FullAccess { get; init; }
    public ExternalDirectoryPermissions ExternalDirectoryPermissions { get; init; } = new();
    public int MaxToolCalls { get; init; } = int.MaxValue;
    public int MaxContextCharacters { get; init; } = 40000000;
    public TimeSpan RunTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan CodeTimeout { get; init; } = TimeSpan.FromSeconds(60);
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
        if (MaxToolCalls < 1 || MaxContextCharacters < 8000 ||
            RunTimeout != Timeout.InfiniteTimeSpan && RunTimeout <= TimeSpan.Zero || CodeTimeout <= TimeSpan.Zero || SandboxStartupTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid agent execution limits.");
        if (!Directory.Exists(WorkspaceRoot)) throw new DirectoryNotFoundException(WorkspaceRoot);
        if (Checkpoints != null && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot)), Checkpoints.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Checkpoint workspace differs from the agent workspace.");
    }
}
