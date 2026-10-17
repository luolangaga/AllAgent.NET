using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AllAgent.NET.Core.Runtime;

public sealed record PowerShellRunOutcome(bool Ok, bool TimedOut, bool SessionRestarted, JsonElement Data, string? Error);

/// <summary>Named persistent PowerShell sessions backed by one bundled worker process each.</summary>
public sealed class PowerShellSessions(string workerPath, string moduleRoot, string rootDirectory) : IAsyncDisposable
{
    public const int MaxSessions = 8;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private const int MaxConsoleCharacters = 20_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _registry = new(1, 1);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<PowerShellRunOutcome> RunAsync(string name, string script, string output, bool reset, string directory, TimeSpan timeout, CancellationToken ct)
    {
        Session session;
        bool restarted;
        await _registry.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PowerShellSessions));
            SweepLocked();
            restarted = _retired.Remove(name);
            if (!_sessions.TryGetValue(name, out var existing))
            {
                EvictLocked();
                existing = await Session.StartAsync(workerPath, moduleRoot, rootDirectory, StartupTimeout, ct).ConfigureAwait(false);
                _sessions[name] = existing;
            }
            session = existing;
        }
        finally { _registry.Release(); }

        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await session.RunAsync(script, output, reset, directory, timeout, ct).ConfigureAwait(false) with { SessionRestarted = restarted };
        }
        catch (SessionTimeoutException)
        {
            await RetireAsync(name, session).ConfigureAwait(false);
            return new(false, true, restarted, default, "PowerShell script exceeded the timeout; the session was reset.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RetireAsync(name, session).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await RetireAsync(name, session).ConfigureAwait(false);
            return new(false, false, restarted, default, "The PowerShell session process stopped unexpectedly; the session was reset.");
        }
        finally { session.Gate.Release(); }
    }

    private void SweepLocked()
    {
        var limit = DateTime.UtcNow - IdleLifetime;
        foreach (var pair in _sessions.Where(pair => pair.Value.LastUsed < limit && pair.Value.Gate.CurrentCount == 1).ToArray())
        {
            _sessions.Remove(pair.Key);
            _retired.Add(pair.Key);
            _ = pair.Value.DisposeAsync();
        }
    }

    private void EvictLocked()
    {
        if (_sessions.Count < MaxSessions) return;
        foreach (var pair in _sessions.Where(pair => pair.Value.Gate.CurrentCount == 1).OrderBy(pair => pair.Value.LastUsed).ToArray())
        {
            _sessions.Remove(pair.Key);
            _retired.Add(pair.Key);
            _ = pair.Value.DisposeAsync();
            if (_sessions.Count < MaxSessions) return;
        }
    }

    private async Task RetireAsync(string name, Session session)
    {
        await _registry.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(name, out var current) && ReferenceEquals(current, session)) _sessions.Remove(name);
            if (!_disposed) _retired.Add(name);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _registry.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        List<Session> sessions;
        await _registry.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
            _retired.Clear();
        }
        finally { _registry.Release(); }
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        _registry.Dispose();
    }

    private sealed class SessionTimeoutException : Exception;
    private sealed class PowerShellSessionException(string message) : InvalidOperationException(message);

    private sealed class Session : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task<(string Text, bool Truncated)> _stderr;
        private long _id;
        private bool _disposed;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

        private Session(Process process)
        {
            _process = process;
            _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            _output = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
            _stderr = BoundedProcess.DrainAsync(process.StandardError, 4_000, _lifetime.Token);
        }

        public static async Task<Session> StartAsync(string workerPath, string moduleRoot, string directory, TimeSpan timeout, CancellationToken ct)
        {
            var info = BoundedProcess.StartInfo(workerPath, ["--serve"], directory);
            info.Environment["ALLAGENT_PS_MODULE_ROOT"] = moduleRoot;
            info.RedirectStandardInput = true;
            info.StandardInputEncoding = new UTF8Encoding(false);
            var process = new Process { StartInfo = info };
            try { process.Start(); }
            catch (Exception ex) { process.Dispose(); throw new PowerShellSessionException("The embedded PowerShell worker could not start: " + ex.Message); }
            var session = new Session(process);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(timeout);
                var line = await session._output.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line == null || !IsReady(line)) throw new PowerShellSessionException("The embedded PowerShell worker did not report ready.");
                return session;
            }
            catch (Exception ex)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new PowerShellSessionException(ex is OperationCanceledException ? "Timed out waiting for the embedded PowerShell worker to start." : ex.Message);
            }
        }

        private static bool IsReady(string line)
        {
            try { using var document = JsonDocument.Parse(line); return document.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "ready"; }
            catch (JsonException) { return false; }
        }

        public async Task<PowerShellRunOutcome> RunAsync(string script, string output, bool reset, string directory, TimeSpan timeout, CancellationToken ct)
        {
            LastUsed = DateTime.UtcNow;
            var id = ++_id;
            var frame = JsonSerializer.Serialize(new { id, kind = "run", script, output, reset, workingDirectory = directory }, JsonOptions);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
            try
            {
                await _input.WriteLineAsync(frame.AsMemory(), deadline.Token).ConfigureAwait(false);
                await _input.FlushAsync(deadline.Token).ConfigureAwait(false);
                var console = new StringBuilder();
                var consoleTruncated = false;
                while (true)
                {
                    var line = await _output.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    if (line == null) throw new IOException("The embedded PowerShell worker closed its output stream.");
                    if (line.Length == 0) continue;
                    if (!TryParseFrame(line, id, out var outcome, out var frameError))
                    {
                        console.Append(line).Append('\n');
                        if (console.Length > MaxConsoleCharacters) { console.Length = MaxConsoleCharacters; consoleTruncated = true; }
                        continue;
                    }
                    if (frameError != null) throw new PowerShellSessionException(frameError);
                    return outcome with { Data = MergeConsole(outcome.Data, console.ToString(), consoleTruncated) };
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill();
                throw new SessionTimeoutException();
            }
            catch (OperationCanceledException)
            {
                Kill();
                throw;
            }
            catch (Exception ex) when (ex is not SessionTimeoutException and not OperationCanceledException)
            {
                Kill();
                throw new PowerShellSessionException(ex.Message);
            }
            finally { LastUsed = DateTime.UtcNow; }
        }

        // Raw output (for example [Console]::WriteLine or unpiped native commands) shares the
        // protocol stream; keep it as consoleOutput instead of failing the call.
        private static bool TryParseFrame(string line, long id, out PowerShellRunOutcome outcome, out string? error)
        {
            outcome = default!;
            error = null;
            if (line[0] != '{') return false;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return false; }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("kind", out var kindElement)) return false;
                var kind = kindElement.GetString();
                if (kind == "error")
                {
                    error = root.TryGetProperty("error", out var message) ? message.GetString() ?? "PowerShell protocol error." : "PowerShell protocol error.";
                    return true;
                }
                if (kind != "result") return false;
                if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt64() != id) throw new PowerShellSessionException("The PowerShell protocol is out of sync; the session was reset.");
                var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
                var failure = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;
                outcome = new(ok, false, false, data, failure);
                return true;
            }
        }

        private static JsonElement MergeConsole(JsonElement data, string console, bool truncated)
        {
            if (console.Length == 0) return data;
            var node = data.ValueKind == JsonValueKind.Object ? JsonNode.Parse(data.GetRawText())!.AsObject() : new JsonObject();
            node["consoleOutput"] = console;
            if (truncated) node["consoleTruncated"] = true;
            return JsonSerializer.SerializeToElement(node, JsonOptions);
        }

        private void Kill()
        {
            try { BoundedProcess.Kill(_process); } catch (Exception) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Kill();
            _lifetime.Cancel();
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            try { _input.Dispose(); } catch (Exception) { }
            try { _output.Dispose(); } catch (Exception) { }
            try { _process.Dispose(); } catch (Exception) { }
            _lifetime.Dispose();
        }
    }
}
