using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

internal sealed record ToolInvocationContext(Guid? ConversationId, string? InvocationId, string? ParentId, string? RunId = null);

internal interface IHostToolGateway
{
    ValueTask<ToolResult> InvokeAsync(ToolInvocationContext context, string name, JsonElement arguments, CancellationToken ct);
}

/// <summary>The only model-originated route to the registry, including calls nested in parallel batches.</summary>
internal sealed class HostToolGateway(Func<string, JsonElement, CancellationToken, ValueTask<ToolResult>> invoke) : IHostToolGateway
{
    public ValueTask<ToolResult> InvokeAsync(ToolInvocationContext context, string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.ConversationId != AgentExecutionScope.ConversationId || context.RunId != AgentExecutionScope.RunId)
            return ValueTask.FromResult(ToolResult.Failure("Tool conversation context changed; operation was not executed."));
        return invoke(name, arguments, ct);
    }

    internal static ToolResult? CheckAvailability(AgentToolDescriptor tool, IReadOnlyList<string>? allowed, bool readOnly, bool simple = false)
    {
        if (simple && !SimpleModeTools.Names.Contains(tool.Name)) return ToolResult.Failure("This host tool is disabled in simple mode.");
        var canonical = tool.Name switch { "file_read" => "read", "file_write" => "write", "file_edit" => "edit", "file_search" => "grep", "list_directory" => "ls", _ => tool.Name };
        if (allowed != null && !allowed.Contains(tool.Name, StringComparer.Ordinal) && !allowed.Contains(canonical, StringComparer.Ordinal))
            return ToolResult.Failure("The current host role does not allow this tool. Do not bypass this restriction.");
        return readOnly && (tool.Risk != ToolRisk.Read || tool.Name is "subagent" or "cancel_agent")
            ? ToolResult.Failure("Current host role is read-only; this operation is not allowed.") : null;
    }
}
