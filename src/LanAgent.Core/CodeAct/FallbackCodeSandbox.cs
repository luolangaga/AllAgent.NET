using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;

namespace LanAgent.Core.CodeAct;

/// <summary>Select once before model execution. Never replay code after a runtime failure.</summary>
public sealed class FallbackCodeSandbox(AgentOptions options) : ICodeSandbox
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICodeSandbox? _selected;
    private bool _disposed;
    public string Name { get; private set; } = "尚未初始化";
    public string? FallbackReason { get; private set; }

    public async Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_selected != null) return await _selected.PrepareAsync(tools, ct);
            if (options.SandboxMode != SandboxMode.Monty)
            {
                var primary = new HyperlightProcessSandbox(options);
                try
                {
                    var context = await primary.PrepareAsync(tools, ct);
                    _selected = primary; Name = "Hyperlight microVM";
                    return context;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    FallbackReason = ex.Message;
                    await primary.DisposeAsync();
                    if (options.SandboxMode == SandboxMode.Hyperlight)
                        throw new InvalidOperationException("指定的 Hyperlight 沙箱不可用：" + ex.Message + "。可使用 /sandbox auto 或 /sandbox monty 切换。", ex);
                }
                catch { await primary.DisposeAsync(); throw; }
            }
            ct.ThrowIfCancellationRequested();
            var fallback = new MontyProcessSandbox(options);
            try
            {
                var context = await fallback.PrepareAsync(tools, ct);
                _selected = fallback; Name = "Monty（无需虚拟化）";
                return context;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await fallback.DisposeAsync();
                throw new InvalidOperationException((FallbackReason == null ? "指定的 Monty 沙箱不可用。" : $"两个沙箱均不可用。Hyperlight: {FallbackReason}\n") + $"Monty: {ex.Message}。请检查网络、Python 和安装权限后运行 /doctor install。", ex);
            }
            catch { await fallback.DisposeAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
    {
        if (_selected == null) await PrepareAsync(tools, ct);
        return await _selected!.ExecuteAsync(code, tools, callback, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; if (_selected != null) await _selected.DisposeAsync(); }
        finally { _gate.Release(); }
    }
}
