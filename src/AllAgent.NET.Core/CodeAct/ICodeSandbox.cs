using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.CodeAct;

public delegate ValueTask<ToolResult> HostToolCallback(string name, JsonElement arguments, CancellationToken ct);

public sealed record SandboxCapabilities(string Backend, bool HardwareIsolation, bool PersistentState, bool FileMounts, bool Network);
public sealed record CodeSandboxContext(string Instructions, string ExecuteCodeDescription)
{
    public SandboxCapabilities? Capabilities { get; init; }
}

public interface ICodeSandbox : IAsyncDisposable
{
    // Custom library sandboxes can supply their own context; Hyperlight uses the SDK's actual guidance.
    Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) => Task.FromResult<CodeSandboxContext?>(null);
    Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct);
}
