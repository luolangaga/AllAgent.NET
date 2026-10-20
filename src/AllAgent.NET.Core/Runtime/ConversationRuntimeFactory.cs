using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

/// <summary>Owned by one data/workspace host. Per-conversation gates prevent cross-session VM,
/// PowerShell and disclosure sharing; views do not own or dispose these runtimes.</summary>
public sealed class ConversationRuntimeFactory : IAsyncDisposable
{
    private sealed class Entry(ToolSurface? surface = null)
    {
        internal readonly ToolSurface Surface = surface ?? new();
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal AgentRuntime? Runtime;
        internal string Fingerprint = "";
    }
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;
    public bool HasActiveRuns => _entries.Values.Any(entry => entry.Runtime?.CodeAct.IsRunning == true);

    public ToolSurface SurfaceFor(string workspace, Guid conversationId) => _entries.GetOrAdd(Path.GetFullPath(workspace) + "|" + conversationId, _ => new()).Surface;

    public async Task<AgentRuntime> GetAsync(Guid conversationId, AgentOptions options, Action<AgentRuntime>? initialize = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        options.Validate();
        var key = Path.GetFullPath(options.WorkspaceRoot) + "|" + conversationId;
        var entry = _entries.GetOrAdd(key, _ => new(options.ToolSurface));
        options = options with { ToolSurface = entry.Surface };
        var fingerprint = JsonSerializer.Serialize(new { options.SandboxMode, options.CurrentPermission, options.SimpleModeActive, options.IsSubAgent,
            options.PythonPath, options.PowerShellPath, options.SandboxWorkerPath, options.PythonGuestPath, options.MaxCodeCharacters,
            options.SharedDirectory, options.FileReadMaxBytes, options.FileReadMaxLines, options.MaxToolResultCharacters, options.MaxToolStreamCharacters,
            options.CodeTimeout, options.SandboxStartupTimeout, options.AllowSandboxInstallation, options.AuditPath, options.ShellAllowlist,
            identities = new object?[] { options.Skills, options.ToolSurface, options.Checkpoints, options.Hooks, options.BackgroundTasks,
                options.BrowserUse, options.ComputerUse, options.ExternalDirectoryPermissions }.Select(value => value == null ? 0 : RuntimeHelpers.GetHashCode(value)),
            tools = options.HostToolsProvider?.Invoke() }, ToolResult.JsonOptions);
        await entry.Gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (entry.Runtime?.CodeAct.IsRunning == true) throw new InvalidOperationException("This conversation already has an active run in another host or view.");
            if (entry.Runtime != null && entry.Fingerprint != fingerprint)
            { await entry.Runtime.DisposeAsync(); entry.Runtime = null; }
            entry.Runtime ??= await AgentRuntime.CreateAsync(options, ct);
            entry.Runtime.RefreshOptions(options);
            entry.Fingerprint = fingerprint;
            using (entry.Runtime.Tools.RefreshRegistration()) initialize?.Invoke(entry.Runtime);
            entry.Runtime.RefreshToolSurface();
            return entry.Runtime;
        }
        finally { entry.Gate.Release(); }
    }

    public async ValueTask ReleaseAsync(Guid conversationId)
    {
        foreach (var pair in _entries.Where(pair => pair.Key.EndsWith("|" + conversationId, StringComparison.OrdinalIgnoreCase)))
        {
            await pair.Value.Gate.WaitAsync();
            try
            {
                if (pair.Value.Runtime?.CodeAct.IsRunning == true) throw new InvalidOperationException("Cannot release a running conversation runtime.");
                if (pair.Value.Runtime != null) await pair.Value.Runtime.DisposeAsync(); pair.Value.Runtime = null;
                pair.Value.Surface.Sync([]);
            }
            finally { pair.Value.Gate.Release(); }
        }
    }

    internal AgentRuntime? Find(Guid conversationId) => _entries.FirstOrDefault(pair => pair.Key.EndsWith("|" + conversationId, StringComparison.OrdinalIgnoreCase)).Value?.Runtime;

    public async ValueTask ClearAsync()
    {
        foreach (var entry in _entries.Values)
        {
            await entry.Gate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _disposed) == 0 && entry.Runtime?.CodeAct.IsRunning == true) continue;
                if (entry.Runtime != null) await entry.Runtime.DisposeAsync(); entry.Runtime = null;
            }
            finally { entry.Gate.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await ClearAsync();
    }
}
