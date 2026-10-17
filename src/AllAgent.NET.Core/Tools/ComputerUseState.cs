namespace AllAgent.NET.Core.Tools;

public enum ComputerInteractionMode { BackgroundOnly, ForegroundAllowed, IsolatedSession }

/// <summary>Shared by parent and child runtimes; never controlled by a model tool.</summary>
public sealed class ComputerUseState
{
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private bool _enabled;
    private ComputerInteractionMode _mode;
    public string WorkerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "computer", "AllAgent.NET.ComputerUse.exe");
    public Uri? IsolatedEndpoint { get; set; }
    public string? IsolatedTokenFile { get; set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal ComputerUseActivity Activity { get; } = new();
    /// <summary>Keep the desktop indicator visible from the first input until this task ends.</summary>
    public IDisposable BeginActivityScope(CancellationToken ct = default) => Activity.BeginScope(ct);
    public (bool Enabled, ComputerInteractionMode Mode, CancellationToken Token) Snapshot()
    { lock (_sync) return (_enabled, _mode, _generation.Token); }
    public void Configure(bool enabled, ComputerInteractionMode mode)
    {
        lock (_sync)
        {
            _generation.Cancel();
            Activity.Stop();
            _generation = new();
            _enabled = enabled; _mode = mode;
        }
    }
    public bool TrySelectMode(ComputerInteractionMode mode)
    {
        lock (_sync)
        {
            if (!_enabled) return false;
            if (_mode == mode) return true;
            _generation.Cancel(); _generation = new(); _mode = mode;
            return true;
        }
    }
}
