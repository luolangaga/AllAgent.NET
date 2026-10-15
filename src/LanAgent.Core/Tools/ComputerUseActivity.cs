using System.Diagnostics;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.Tools;

// Turn scopes retain the indicator. The UI process debounces idle/stop for three seconds,
// and accepts a new active message during that delay without recreating its windows.
internal sealed class ComputerUseActivity
{
    private readonly object _sync = new();
    private int _scopes, _operations;
    private bool _stopped;
    private Indicator? _indicator;

    public IDisposable BeginScope(CancellationToken ct)
    {
        lock (_sync) _scopes++;
        var cancellation = ct.Register(Stop);
        return new Lease(() => { cancellation.Unregister(); lock (_sync) { _scopes--; CloseIfIdle(); } });
    }

    public async Task<IDisposable> BeginOperationAsync(string workerPath, ComputerInteractionMode mode, CancellationToken ct)
    {
        Indicator indicator;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_indicator == null || _indicator.HasExited)
            {
                _indicator?.Dispose();
                _indicator = new Indicator(workerPath, mode);
            }
            indicator = _indicator;
            _operations++;
            _stopped = false;
        }
        var lease = new Lease(() => { lock (_sync) { _operations--; CloseIfIdle(); } });
        try
        {
            await indicator.Ready.WaitAsync(ct).ConfigureAwait(false);
            if (!await indicator.ActivateAsync(mode, ct).ConfigureAwait(false))
            {
                // A process may still be alive for a moment after its windows close. Require
                // the UI's acknowledgement, rather than trusting HasExited or a buffered write.
                lock (_sync)
                {
                    indicator.Dispose();
                    _indicator = indicator = new Indicator(workerPath, mode);
                }
                await indicator.Ready.WaitAsync(ct).ConfigureAwait(false);
                if (!await indicator.ActivateAsync(mode, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Computer Use indicator did not acknowledge activity; desktop input was not sent.");
            }
            return lease;
        }
        catch { lock (_sync) { _indicator?.Dispose(); _indicator = null; } lease.Dispose(); throw; }
    }

    public void Stop()
    { lock (_sync) { _stopped = true; _indicator?.Send("stop"); } }

    private void CloseIfIdle()
    { if (_scopes == 0 && _operations == 0) _indicator?.Send(_stopped ? "stop" : "idle"); }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class Indicator : IDisposable
    {
        private readonly Process _process;
        private readonly object _pipeSync = new();
        public Task Ready { get; }
        public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }

        public Indicator(string path, ComputerInteractionMode mode)
        {
            var info = BoundedProcess.StartInfo(path, ["--overlay", mode.ToString()]);
            info.RedirectStandardInput = true;
            _process = new Process { StartInfo = info };
            try { _process.Start(); }
            catch { _process.Dispose(); throw; }
            Ready = WaitReadyAsync();
        }

        public async Task<bool> ActivateAsync(ComputerInteractionMode mode, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!Send("active\t" + mode)) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            try { return await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) == "active"; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return false; }
        }
        public bool Send(string command)
        {
            lock (_pipeSync)
            {
                try { if (HasExited) return false; _process.StandardInput.WriteLine(command); _process.StandardInput.Flush(); return true; }
                catch (IOException) { return false; }
                catch (InvalidOperationException) { return false; }
            }
        }

        private async Task WaitReadyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "ready")
                    throw new InvalidOperationException("Computer Use indicator could not be displayed; desktop input was not sent.");
            }
            catch (OperationCanceledException)
            { throw new InvalidOperationException("Computer Use indicator did not become ready; desktop input was not sent."); }
        }

        public void Dispose()
        {
            lock (_pipeSync)
            { try { _process.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { } }
            // EOF gives the UI a bounded minimum display + fade. Never block an input action
            // for that grace period, and still reap/kill the process if the UI stops responding.
            _ = ReapAsync();
        }

        private async Task ReapAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { BoundedProcess.Kill(_process); }
            catch (InvalidOperationException) { }
            finally { _process.Dispose(); }
        }
    }
}
