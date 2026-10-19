namespace AllAgent.NET.Core.Tools;

public enum ComputerInteractionMode { BackgroundOnly, ForegroundAllowed, IsolatedSession }

/// <summary>Shared by parent and child runtimes; never controlled by a model tool. Always available:
/// the user-facing enable switch was removed, the computer-use skill is the on-demand entry point and
/// per-call approvals plus role policies still gate every action.</summary>
public sealed class ComputerUseState
{
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private ComputerInteractionMode _mode;
    public string WorkerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.ComputerUse.exe");
    public Uri? IsolatedEndpoint { get; set; }
    public string? IsolatedTokenFile { get; set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal ComputerUseActivity Activity { get; } = new();
    /// <summary>Keep the desktop indicator visible from the first input until this task ends.</summary>
    public IDisposable BeginActivityScope(CancellationToken ct = default) => Activity.BeginScope(ct);
    public (ComputerInteractionMode Mode, CancellationToken Token) Snapshot()
    { lock (_sync) return (_mode, _generation.Token); }
    /// <summary>Select the mode and revoke operations started under the previous one. Also stops the desktop indicator.</summary>
    public void Configure(ComputerInteractionMode mode)
    {
        lock (_sync)
        {
            _generation.Cancel();
            Activity.Stop();
            _generation = new();
            _mode = mode;
        }
    }
    public bool TrySelectMode(ComputerInteractionMode mode)
    {
        lock (_sync)
        {
            if (_mode == mode) return true;
            _generation.Cancel(); _generation = new(); _mode = mode;
            return true;
        }
    }
}
