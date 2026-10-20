using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.CodeAct;

/// <summary>Dedicated worker owns the native microVM. Cancellation kills the entire worker tree.</summary>
public sealed class HyperlightProcessSandbox(AgentOptions options) : ICodeSandbox
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private Task<(string Text, bool Truncated)>? _stderr;
    private bool _disposed;
    private string? _toolsFingerprint;
    private CodeSandboxContext? _context;
    private string? _executionId;
    private Timer? _idle;
    private long _lastUse = Stopwatch.GetTimestamp();

    public async Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_disposed && _worker is { HasExited: false } && _context != null && _toolsFingerprint == Fingerprint(tools)) return _context;
        }
        finally { _gate.Release(); }
        var result = await ExecuteAsync("print('allagent-context-ready')", tools,
            (_, _, _) => new(ToolResult.Failure("Host operations are unavailable during context preparation.")), ct);
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return _context ?? throw new InvalidOperationException("Hyperlight did not provide its CodeAct instructions.");
    }

    public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeTime = new Stopwatch();
        deadline.CancelAfter(options.SandboxStartupTimeout);
        try
        {
            _idle ??= new Timer(_ => ReleaseIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var fingerprint = Fingerprint(tools);
            if (_toolsFingerprint != fingerprint) StopWorker();
            if (_worker == null || _worker.HasExited)
            {
                StopWorker();
                if (!File.Exists(options.SandboxWorkerPath)) return ToolResult.Failure("Hyperlight worker is missing. Build or publish the terminal/desktop host with its sandbox folder.");
                var info = BoundedProcess.StartInfo(options.SandboxWorkerPath, [], Path.GetDirectoryName(options.SandboxWorkerPath));
                info.RedirectStandardInput = true;
                // The worker and native guest never inherit model-provider credentials.
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "USERPROFILE", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" };
                foreach (var key in info.Environment.Keys.ToArray()) if (!allowed.Contains(key)) info.Environment.Remove(key);
                // Each worker serializes one VM; do not prewarm the SDK's 512-process pool.
                info.Environment["HYPERLIGHT_INITIAL_SURROGATES"] = "1";
                info.Environment["HYPERLIGHT_MAX_SURROGATES"] = "2";
                _worker = Process.Start(info) ?? throw new InvalidOperationException("Cannot start sandbox worker.");
                _toolsFingerprint = fingerprint;
                _stderr = BoundedProcess.DrainAsync(_worker.StandardError, 4000, CancellationToken.None);
                _executionId = null;
                await WriteAsync(new { kind = "init", guestPath = options.PythonGuestPath, tools }, deadline.Token);
                using var ready = await ReadAsync(deadline.Token);
                if (ready.RootElement.GetProperty("kind").GetString() != "ready")
                    throw new InvalidOperationException(ready.RootElement.TryGetProperty("error", out var initError) ? initError.GetString() : "Sandbox initialization failed.");
                _context = new(ready.RootElement.GetProperty("instructions").GetString()!, ready.RootElement.GetProperty("description").GetString()!)
                { Capabilities = new("hyperlight", true, false, false, false) };
            }
            activeTime.Start(); deadline.CancelAfter(options.CodeTimeout);
            _executionId = Guid.NewGuid().ToString("N");
            await WriteAsync(new { kind = "execute", code }, deadline.Token);
            while (true)
            {
                using var message = await ReadAsync(deadline.Token);
                var root = message.RootElement;
                switch (root.GetProperty("kind").GetString())
                {
                    case "call":
                        var name = root.GetProperty("name").GetString()!;
                        if (!tools.Any(tool => tool.Name == name)) throw new InvalidDataException("Unknown sandbox host tool.");
                        var callId = root.GetProperty("callId").GetString() ?? throw new InvalidDataException("Missing host call ID.");
                        // The guest is suspended during a host call. Human questions and approval waits
                        // must not consume the Python execution budget; the overall agent token still applies.
                        deadline.Token.ThrowIfCancellationRequested();
                        deadline.CancelAfter(Timeout.InfiniteTimeSpan); activeTime.Stop();
                        var result = await callback(name, root.GetProperty("arguments").Clone(), ct);
                        activeTime.Start();
                        var remaining = options.CodeTimeout - activeTime.Elapsed;
                        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
                        deadline.CancelAfter(remaining);
                        await WriteAsync(new { kind = "tool_result", result }, deadline.Token, callId);
                        break;
                    case "result":
                        var execution = root.GetProperty("result").Deserialize<ToolResult>(ToolResult.JsonOptions) ?? ToolResult.Failure("Invalid sandbox result.");
                        if (!execution.Ok && IsNativeRuntimeFailure(execution))
                        {
                            StopWorker();
                            execution = execution with { Error = execution.Error + " The native worker was discarded; the next call will start a fresh microVM. This execution was not retried; earlier host tool side effects may remain." };
                        }
                        return execution;
                    case "error":
                        throw new InvalidOperationException(root.GetProperty("error").GetString());
                    default: throw new InvalidOperationException("Unexpected sandbox protocol message.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            ct.ThrowIfCancellationRequested();
            return ToolResult.Failure(activeTime.Elapsed == TimeSpan.Zero ? "Hyperlight startup timed out. The microVM worker was terminated." : "Code execution timed out. The microVM worker was terminated; earlier host tool side effects may remain.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            StopWorker();
            return ToolResult.Failure("Hyperlight unavailable: " + ex.Message);
        }
        finally { _lastUse = Stopwatch.GetTimestamp(); _gate.Release(); }
    }

    private static bool IsNativeRuntimeFailure(ToolResult result)
    {
        // A Python exception merely quoting these words is an ordinary guest failure.
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("diagnostic", out var diagnostic) && diagnostic.ValueKind == JsonValueKind.Object) return false;
        var error = result.Error;
        return error != null && (error.Contains("no reactor running", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("poisoned lock", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Error locking host_funcs", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("restore failed", StringComparison.OrdinalIgnoreCase));
    }

    private async Task WriteAsync(object value, CancellationToken ct, string? callId = null)
    {
        await _worker!.StandardInput.WriteLineAsync(SandboxProtocol.Encode(value, _executionId, callId).AsMemory(), ct);
        await _worker.StandardInput.FlushAsync(ct);
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken ct)
    {
        // Call frames carry tool inputs, including complete file contents. Output
        // preview budgets must not become an input-size restriction on this channel.
        var frame = await SandboxProtocol.ReadAsync(_worker!.StandardOutput.BaseStream, ct);
        try { SandboxProtocol.Validate(frame.RootElement, _executionId); return frame; }
        catch { frame.Dispose(); throw; }
    }

    private void StopWorker()
    {
        if (_worker == null) return;
        BoundedProcess.Kill(_worker);
        // Kill is asynchronous on Windows. Join termination before releasing the
        // worker or starting its replacement so native/file handles are gone.
        _worker.WaitForExit();
        _worker.Dispose();
        _worker = null;
        _context = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; _idle?.Dispose(); StopWorker(); if (_stderr != null) await _stderr; }
        finally { _gate.Release(); }
    }

    private static string Fingerprint(IReadOnlyList<AgentToolDescriptor> tools) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tools, ToolResult.JsonOptions))));
    private void ReleaseIdle()
    {
        if (!_gate.Wait(0)) return;
        try { if (!_disposed && Stopwatch.GetElapsedTime(_lastUse) >= TimeSpan.FromMinutes(5)) StopWorker(); }
        finally { _gate.Release(); }
    }
}
