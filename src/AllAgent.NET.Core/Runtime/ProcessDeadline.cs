using System.Diagnostics;

namespace AllAgent.NET.Core.Runtime;

// Timer implementations have a maximum delay; chunking keeps that implementation
// detail from imposing an upper bound on a caller's requested timeout.
internal sealed class ProcessDeadline : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly Timer? _timer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout;
    public CancellationToken Token => _source.Token;
    public ProcessDeadline(CancellationToken ct, TimeSpan timeout)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(ct); _timeout = timeout;
        if (timeout == Timeout.InfiniteTimeSpan) return;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timer = new Timer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(Slice(timeout), Timeout.InfiniteTimeSpan);
    }
    private static TimeSpan Slice(TimeSpan value) => value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : value;
    private void Tick(object? state)
    {
        var remaining = _timeout - Stopwatch.GetElapsedTime(_started);
        try
        {
            if (remaining <= TimeSpan.Zero) _source.Cancel();
            else _timer!.Change(Slice(remaining), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { _timer?.Dispose(); _source.Dispose(); }
}
