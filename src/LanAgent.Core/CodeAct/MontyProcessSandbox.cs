using System.Diagnostics;
using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;

namespace LanAgent.Core.CodeAct;

/// <summary>Monty's Rust interpreter supplies isolation; CPython runs only trusted IPC code.</summary>
public sealed class MontyProcessSandbox(AgentOptions options) : ICodeSandbox
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string? _python;
    private const string Guidance = """
        Active sandbox: Monty (no hardware virtualization required). Each execution has fresh state.
        Only a subset of Python is implemented. Use simple functions, loops, lists and dicts.
        Do not assume CPython's os.path or host filesystem APIs exist. Use file tools for paths and metadata; an AttributeError for a module member indicates an unsupported sandbox API.
        Modules are not automatically imported. Write import json before using json.loads or json.dumps in each execution.
        No third-party imports, struct, generators/yield, inheritance or match. Unsupported modules fail at runtime.
        There are no filesystem mounts or network capabilities. Use call_tool(name, **kwargs) for ALL host I/O.
        call_tool returns the standard LanAgent ok/data/error/truncated object. Print essential output.
        Host Python libraries are separate and remain accessible only through approved host tools.
        """;

    public async Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var probe = await ExecuteAsync("print(1 + 1)", tools,
            (_, _, _) => new(ToolResult.Failure("Host calls unavailable during preparation.")), ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error);
        return new(Guidance, "Execute Python subset in an isolated Monty interpreter.");
    }

    private async Task EnsureInstalledAsync(CancellationToken ct)
    {
        if (_python != null) return;
        await InstallGate.WaitAsync(ct);
        try
        {
            var root = Path.Combine(options.SharedDirectory, "runtimes", "monty-1.1.0");
            var python = Path.Combine(root, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
            async Task Run(string exe, string[] args)
            {
                var result = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(exe, args), TimeSpan.FromMinutes(5), ct);
                if (result.ExitCode != 0) throw new InvalidOperationException("Monty 安装失败：" + ToolResult.Clip(result.Stderr + result.Stdout));
            }
            if (!File.Exists(python) && !options.AllowSandboxInstallation)
                throw new InvalidOperationException("备用 Monty 尚未安装；运行 /doctor install 联网安装，无需管理员权限或虚拟化。");
            if (!File.Exists(python))
                await Run(options.PythonPath ?? throw new InvalidOperationException("Monty 需要宿主 Python。"), ["-I", "-m", "venv", root]);
            var check = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(python,
                ["-I", "-c", "import importlib.metadata; import pydantic_monty; assert importlib.metadata.version('pydantic-monty') == '1.1.0'"]), TimeSpan.FromSeconds(15), ct);
            if (check.ExitCode != 0 && !options.AllowSandboxInstallation)
                throw new InvalidOperationException("备用 Monty 依赖不完整；运行 /doctor install 修复。");
            if (check.ExitCode != 0)
                await Run(python, ["-I", "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--only-binary=:all:", "pydantic-monty==1.1.0"]);
            _python = python;
        }
        finally { InstallGate.Release(); }
    }

    public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_python == null) await EnsureInstalledAsync(ct);
            var info = BoundedProcess.StartInfo(_python!, ["-I", "-u", "-c", RuntimeScripts.Read("monty_worker.py")], Path.GetDirectoryName(_python));
            info.RedirectStandardInput = true;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "USERPROFILE", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" };
            foreach (var key in info.Environment.Keys.ToArray()) if (!allowed.Contains(key)) info.Environment.Remove(key);
            using var worker = Process.Start(info) ?? throw new InvalidOperationException("Cannot start Monty worker.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.SandboxStartupTimeout);
            var stderr = BoundedProcess.DrainAsync(worker.StandardError, 4000, CancellationToken.None);
            var active = Stopwatch.StartNew();
            async Task Send(object message)
            {
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, ToolResult.JsonOptions).AsMemory(), deadline.Token);
                await worker.StandardInput.FlushAsync(deadline.Token);
            }
            try
            {
                await Send(new { code, tools = tools.Select(t => t.Name), timeout = options.CodeTimeout.TotalSeconds });
                // Startup and Python execution share a bounded budget; approval waits do not consume it.
                var budget = options.SandboxStartupTimeout + options.CodeTimeout;
                deadline.CancelAfter(budget);
                while (true)
                {
                    var line = await worker.StandardOutput.ReadLineAsync(deadline.Token);
                    if (line == null) throw new IOException("Monty worker exited: " + (await stderr).Text);
                    using var message = JsonDocument.Parse(line);
                    var root = message.RootElement;
                    switch (root.GetProperty("kind").GetString())
                    {
                        case "call":
                            deadline.Token.ThrowIfCancellationRequested();
                            deadline.CancelAfter(Timeout.InfiniteTimeSpan); active.Stop();
                            var name = root.GetProperty("name").GetString()!;
                            var reply = tools.Any(t => t.Name == name)
                                ? await callback(name, root.GetProperty("arguments").Clone(), ct)
                                : ToolResult.Failure("Unknown host tool.");
                            active.Start();
                            var remaining = budget - active.Elapsed;
                            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
                            deadline.CancelAfter(remaining);
                            await Send(new { kind = "tool_result", result = reply });
                            break;
                        case "result":
                            return root.GetProperty("result").Deserialize<ToolResult>(ToolResult.JsonOptions) ?? ToolResult.Failure("Invalid Monty result.");
                        default: throw new IOException("Invalid Monty protocol message.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return ToolResult.Failure("Monty execution timed out; earlier host tool side effects may remain. Execution was not retried.");
            }
            finally
            {
                BoundedProcess.Kill(worker);
                await worker.WaitForExitAsync(CancellationToken.None);
                await stderr;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        { return ToolResult.Failure("Monty unavailable: " + ex.Message); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; } finally { _gate.Release(); }
    }
}
