using System.Text.Json;
using LanAgent.Core.Tools;

namespace LanAgent.Core.CodeAct;

public delegate ValueTask<ToolResult> HostToolCallback(string name, JsonElement arguments, CancellationToken ct);

public sealed record CodeSandboxContext(string Instructions, string ExecuteCodeDescription);

public interface ICodeSandbox : IAsyncDisposable
{
    // Custom library sandboxes can supply their own context; Hyperlight uses the SDK's actual guidance.
    Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) => Task.FromResult<CodeSandboxContext?>(null);
    Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct);
}
