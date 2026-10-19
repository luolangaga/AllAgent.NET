using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Terminal;

/// <summary>One shell a browser terminal tab can launch; discovered, never assumed.</summary>
internal sealed record TerminalShell(string Id, string Label, string CommandLine)
{
    public static readonly IReadOnlyList<TerminalShell> All = Discover();

    private static IReadOnlyList<TerminalShell> Discover()
    {
        var shells = new List<TerminalShell>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsPowerShell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
            shells.Add(new TerminalShell("powershell", "Windows PowerShell",
                TerminalSession.Quote(windowsPowerShell) + " -NoLogo -NoExit -Command \"[Console]::InputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null\""));
        var commandPrompt = Path.Combine(system, "cmd.exe");
        if (File.Exists(commandPrompt))
            shells.Add(new TerminalShell("cmd", "命令提示符", TerminalSession.Quote(commandPrompt) + " /d /k \"chcp 65001>nul\""));
        if (EnvironmentDoctor.FindExecutable("pwsh.exe") is { } pwsh)
            shells.Add(new TerminalShell("pwsh", "PowerShell 7",
                TerminalSession.Quote(pwsh) + " -NoLogo -NoExit -Command \"[Console]::InputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8\""));
        if (EnvironmentDoctor.FindExecutable("bash.exe") is { } bash)
            shells.Add(new TerminalShell("bash", "Git Bash", TerminalSession.Quote(bash) + " --login -i"));
        return shells;
    }
}

/// <summary>Payload one terminal tab reports to the page.</summary>
public sealed record TerminalSessionDto(string Id, string Title, string ShellId, string ShellLabel, string Cwd,
    int Cols, int Rows, bool Alive, int? ExitCode, long Sequence);

/// <summary>
/// Live ConPTY sessions behind the browser's terminal tabs. Output is coalesced into frames pushed
/// to every connected page with a per-session sequence; a bounded replay buffer lets a reloaded page
/// re-attach to the same shell. Sessions are host-level: they survive page reloads, view switches
/// and workspace switches, and die only when the user closes the tab or the host exits.
/// </summary>
internal sealed class WebTerminalManager(BridgeEventPusher pusher, Func<string> defaultCwd) : IAsyncDisposable
{
    private const int MaxSessions = 12;
    private const int ReplayLimit = 256 * 1024;
    private const int FlushThreshold = 64 * 1024;
    private const int FlushIntervalMs = 16;

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _counter;

    public static IReadOnlyList<TerminalShell> Shells => TerminalShell.All;

    public IReadOnlyList<TerminalSessionDto> List()
    {
        lock (_gate) return _sessions.Values.Select(session => session.ToDto()).ToArray();
    }

    public TerminalSessionDto Create(string? shellId, string? cwd, int cols, int rows)
    {
        var shell = TerminalShell.All.FirstOrDefault(item => string.Equals(item.Id, shellId, StringComparison.OrdinalIgnoreCase))
            ?? TerminalShell.All.FirstOrDefault()
            ?? throw new InvalidOperationException("没有可用的 Shell：未找到 Windows PowerShell 或 cmd。");
        var directory = ResolveCwd(cwd);
        Session session;
        lock (_gate)
        {
            if (_sessions.Count >= MaxSessions) throw new InvalidOperationException($"最多同时打开 {MaxSessions} 个终端，请先关闭一些标签。");
            PseudoConsole console;
            try { console = new PseudoConsole(shell.CommandLine, (short)Math.Clamp(cols, 20, 500), (short)Math.Clamp(rows, 5, 200), directory); }
            catch (Exception ex) { throw new InvalidOperationException($"无法启动 {shell.Label}：{ex.Message}"); }
            _counter++;
            var title = _counter == 1 ? shell.Label : $"{shell.Label} ({_counter})";
            session = new Session(this, Guid.NewGuid().ToString("N"), title, shell, directory, console);
            _sessions[session.Id] = session;
        }
        session.Start();
        PushChanged();
        return session.ToDto();
    }

    public (TerminalSessionDto Session, string Replay, long Sequence) Attach(string id)
    {
        var session = Get(id);
        var (replay, sequence) = session.Snapshot();
        return (session.ToDto(), replay, sequence);
    }

    public async Task<bool> WriteAsync(string id, byte[] data, CancellationToken ct)
    {
        var session = Get(id);
        if (!session.Alive) return false;
        await session.WriteAsync(data, ct);
        return true;
    }

    public bool Resize(string id, int cols, int rows)
    {
        var session = Get(id);
        if (!session.Alive) return false;
        session.Resize(cols, rows);
        return true;
    }

    public async Task<bool> CloseAsync(string id, CancellationToken ct)
    {
        Session? session;
        lock (_gate) { if (!_sessions.Remove(id, out session)) return false; }
        await session.DisposeAsync();
        PushChanged();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate) { sessions = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var session in sessions) await session.DisposeAsync();
    }

    private Session Get(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("终端会话 ID 为空。");
        lock (_gate) return _sessions.TryGetValue(id, out var session)
            ? session
            : throw new ArgumentException("终端会话不存在或已关闭：" + id);
    }

    private string ResolveCwd(string? cwd)
    {
        string full;
        try
        {
            // Relative directories resolve against the workspace the page is looking at.
            full = string.IsNullOrWhiteSpace(cwd)
                ? Path.GetFullPath(defaultCwd())
                : Path.IsPathFullyQualified(cwd.Trim()) ? Path.GetFullPath(cwd.Trim()) : Path.GetFullPath(cwd.Trim(), defaultCwd());
        }
        catch (ArgumentException) { throw new InvalidOperationException("终端目录无效：" + cwd); }
        if (!Directory.Exists(full)) throw new InvalidOperationException("终端目录不存在：" + full);
        return full;
    }

    internal void PushOutput(string id, byte[] data, long sequence)
        => Push("terminal.output", new { base64 = Convert.ToBase64String(data) }, id, sequence);

    internal void PushExit(string id, int? exitCode)
        => Push("terminal.exit", new { exitCode }, id, null);

    private void PushChanged() => Push("terminal.changed", new { }, string.Empty, null);

    private void Push(string eventName, object data, string streamId, long? sequence)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                eventName,
                streamId,
                sequence,
                timestamp = DateTime.UtcNow.ToString("o"),
                data,
            }, JsonOptions.Default);
            pusher.Push(payload);
        }
        catch (Exception ex) { Console.Error.WriteLine("[WebTerminal] push failed: " + ex.Message); }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly WebTerminalManager _owner;
        private readonly object _gate = new();
        private readonly List<byte[]> _replay = [];
        private int _replayBytes;
        private readonly List<byte[]> _pending = [];
        private int _pendingBytes;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private long _sequence;
        private Task _pump = Task.CompletedTask;
        private Task _flush = Task.CompletedTask;
        private Task _monitor = Task.CompletedTask;
        private int _finished;
        private volatile bool _closed;

        public Session(WebTerminalManager owner, string id, string title, TerminalShell shell, string cwd, PseudoConsole console)
        {
            _owner = owner;
            Id = id;
            Title = title;
            ShellId = shell.Id;
            ShellLabel = shell.Label;
            Cwd = cwd;
            Console = console;
            Cols = 120;
            Rows = 30;
        }

        public string Id { get; }
        public string Title { get; }
        public string ShellId { get; }
        public string ShellLabel { get; }
        public string Cwd { get; }
        public PseudoConsole Console { get; }
        public short Cols { get; private set; }
        public short Rows { get; private set; }
        public bool Alive { get; private set; } = true;
        public int? ExitCode { get; private set; }

        public void Start()
        {
            _pump = Task.Run(PumpAsync);
            _flush = Task.Run(FlushLoopAsync);
            // ConPTY keeps the output pipe open while the pseudo console exists, so end-of-stream
            // alone is not a reliable exit signal; poll the process like the terminal broker does.
            _monitor = Task.Run(MonitorAsync);
        }

        public TerminalSessionDto ToDto()
        {
            lock (_gate)
                return new TerminalSessionDto(Id, Title, ShellId, ShellLabel, Cwd, Cols, Rows, Alive, ExitCode, _sequence);
        }

        /// <summary>Snapshot for (re-)attaching: everything retained plus the sequence it ends at.</summary>
        public (string Replay, long Sequence) Snapshot()
        {
            lock (_gate)
            {
                FlushLocked();
                var total = new byte[_replayBytes];
                var offset = 0;
                foreach (var chunk in _replay) { Buffer.BlockCopy(chunk, 0, total, offset, chunk.Length); offset += chunk.Length; }
                return (Convert.ToBase64String(total), _sequence);
            }
        }

        public async Task WriteAsync(byte[] data, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                await Console.Input.WriteAsync(data, ct);
                await Console.Input.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { _writeGate.Release(); }
        }

        public void Resize(int cols, int rows)
        {
            Cols = (short)Math.Clamp(cols, 20, 500);
            Rows = (short)Math.Clamp(rows, 5, 200);
            try { Console.Resize(Cols, Rows); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[16384];
            try
            {
                while (true)
                {
                    int count;
                    try { count = await Console.Output.ReadAsync(buffer, _stop.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
                    if (count <= 0) break;
                    Append(buffer.AsSpan(0, count));
                }
            }
            finally { await FinishAsync(fromPump: true); }
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested && !_closed)
                {
                    if (Console.HasExited) { await FinishAsync(fromPump: false); return; }
                    await Task.Delay(100, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task FlushLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(FlushIntervalMs, _stop.Token);
                    Flush();
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            bool flushNow;
            lock (_gate)
            {
                _pending.Add(data.ToArray());
                _pendingBytes += data.Length;
                flushNow = _pendingBytes >= FlushThreshold;
            }
            if (flushNow) Flush();
        }

        private void Flush()
        {
            lock (_gate) FlushLocked();
        }

        /// <summary>Sequence assignment, replay retention and the push all happen under the lock so
        /// frames reach the page in sequence order even with the ticker and threshold racing.</summary>
        private void FlushLocked()
        {
            if (_pendingBytes == 0) return;
            var payload = new byte[_pendingBytes];
            var offset = 0;
            foreach (var chunk in _pending) { Buffer.BlockCopy(chunk, 0, payload, offset, chunk.Length); offset += chunk.Length; }
            _pending.Clear();
            _pendingBytes = 0;
            _sequence++;
            _replay.Add(payload);
            _replayBytes += payload.Length;
            while (_replayBytes > ReplayLimit && _replay.Count > 1) { _replayBytes -= _replay[0].Length; _replay.RemoveAt(0); }
            _owner.PushOutput(Id, payload, _sequence);
        }

        /// <summary>Runs once when the shell ends: drain the last bytes, report the exit code and
        /// release the pseudo console. Closing a user-initiated tab suppresses the exit notice.</summary>
        private async Task FinishAsync(bool fromPump)
        {
            if (_closed || Interlocked.Exchange(ref _finished, 1) == 1) return;
            // Closing the pseudo console ends the blocked output read so the final bytes flush.
            try { Console.CloseSession(); } catch { }
            if (!fromPump)
            {
                try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            Flush();
            int? code = null;
            try { if (Console.HasExited) code = Console.ExitCode; } catch { }
            lock (_gate) { Alive = false; ExitCode = code; }
            _owner.PushExit(Id, code);
            _stop.Cancel();
            try { Console.Dispose(); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed) _closed = true;
            _stop.Cancel();
            try { Console.CloseSession(); } catch { }
            for (var attempt = 0; attempt < 40 && !Console.HasExited; attempt++) await Task.Delay(50);
            if (!Console.HasExited) Console.Kill();
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { await _flush.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            try { await _monitor.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            lock (_gate) Alive = false;
            try { Console.Dispose(); } catch { }
            _writeGate.Dispose();
            _stop.Dispose();
        }
    }
}
