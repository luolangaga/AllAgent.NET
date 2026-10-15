using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;

namespace LanAgent.Core.CodeAct;

/// <summary>Compatibility adapter: all execution now uses Hyperlight, including full access.</summary>
[Obsolete("Use HyperlightProcessSandbox. Full access changes approval, not isolation.")]
public sealed class HostPythonSandbox : ICodeSandbox
{
    private readonly HyperlightProcessSandbox _sandbox;
    public HostPythonSandbox(AgentOptions options) => _sandbox = new(options);
    public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct)
        => _sandbox.PrepareAsync(tools, ct);
    public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        => _sandbox.ExecuteAsync(code, tools, callback, ct);
    public ValueTask DisposeAsync() => _sandbox.DisposeAsync();
}
