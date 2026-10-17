using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

/// <summary>Live read-only restrictions apply even when the parent granted full access.</summary>
public sealed class SubAgentRolePolicy(Func<bool> readOnly, string workspace, ExternalDirectoryPermissions? permissions = null,
    Func<AgentRoleDefinition>? role = null, ToolApprover? approver = null, Func<IReadOnlyList<string>?>? parentTools = null)
{
    private readonly Lazy<WorkspacePolicy> _workspace = new(() => new(workspace, permissions: permissions));
    public ToolResult? Validate(AgentToolDescriptor tool, JsonElement arguments)
    {
        if (Evaluate(tool, arguments).Effect == "deny") return ToolResult.Failure("Current agent permission rules deny this operation. Do not retry or bypass this restriction.");
        if (!readOnly()) return null;
        if (tool.Risk != ToolRisk.Read) return ToolResult.Failure("Current child role is read-only. Do not retry or bypass this restriction.");
        if (tool.Name is "file_read" or "list_directory" or "file_search" or "everything_search" or "read" or "grep" or "find" or "ls")
        {
            try { _workspace.Value.Resolve(ToolArguments.Get(arguments, "path", ".")); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException)
            { return ToolResult.Failure(ex.Message); }
        }
        return null;
    }

    public async ValueTask<ToolResult?> AuthorizeAsync(AgentToolDescriptor tool, JsonElement arguments, CancellationToken ct)
    {
        var denied = Validate(tool, arguments);
        if (denied != null) return denied;
        var decision = Evaluate(tool, arguments);
        if (decision.Effect != "ask") return null;
        var request = new ToolApprovalRequest(tool.Name, tool.Risk, arguments.Clone(), "Agent 角色 " + decision.Name + " 的权限规则要求逐次确认") { RequiresExplicitApproval = true };
        if (approver == null || !await approver(request, ct)) return ToolResult.Failure("Agent permission approval denied. Do not retry or bypass this decision.");
        ct.ThrowIfCancellationRequested();
        if (Evaluate(tool, arguments) != decision) return ToolResult.Failure("Agent permissions changed while awaiting approval. The previous approval no longer authorizes this operation.");
        return Validate(tool, arguments);
    }

    private (string? Name, string Effect, string? Version) Evaluate(AgentToolDescriptor tool, JsonElement arguments)
    {
        var current = role?.Invoke();
        if (parentTools?.Invoke() is { } parent && !parent.Contains(tool.Name, StringComparer.Ordinal))
            return (current?.Name, "deny", null);
        if (tool.Name is "skill_read" or "skill_reference" && current?.Execution?.Skills is { } skills &&
            !skills.Contains(ToolArguments.Get<string>(arguments, "name", ""), StringComparer.OrdinalIgnoreCase))
            return (current.Name, "deny", JsonSerializer.Serialize(current));
        var canonical = tool.Name switch { "file_read" => "read", "file_write" => "write", "file_edit" => "edit", "file_search" => "grep", "everything_search" => "find", "list_directory" => "ls", _ => tool.Name };
        if (current?.Tools is { } tools && !tools.Contains(canonical, StringComparer.Ordinal) && !tools.Contains(tool.Name, StringComparer.Ordinal))
            return (current.Name, "deny", JsonSerializer.Serialize(current));
        if (current?.Permissions == null) return (current?.Name, "allow", null);
        var action = tool.Name switch
        {
            "file_read" => "read", "file_write" => "write", "file_edit" => "edit", "list_directory" or "ls" => "list",
            "run_shell" or "powershell" or "powershell_invoke" or "powershell_command_info" => "bash", "web_fetch" => "webfetch", "web_search" => "websearch",
            "skill_read" or "skill_reference" => "skill", "subagent" or "cancel_agent" => "task", _ => tool.Name
        };
        var resource = tool.Name switch
        {
            "file_read" or "file_write" or "file_edit" or "list_directory" or "file_search" or "everything_search" or "read" or "write" or "edit" or "grep" or "find" or "ls" => _workspace.Value.ResolveForApproval(ToolArguments.Get(arguments, "path", ".")),
            "run_shell" => ToolArguments.Get<string>(arguments, "exe") + " " + string.Join(" ", ToolArguments.Get<string[]>(arguments, "args", [])),
            "powershell_invoke" or "powershell_command_info" => ToolArguments.Get<string>(arguments, "module") + "\\" + ToolArguments.Get<string>(arguments, "command"),
            "bash" or "powershell" => ToolArguments.Get<string>(arguments, "command", ""),
            "web_fetch" => ToolArguments.Get<string>(arguments, "url", "*"),
            "web_search" => ToolArguments.Get<string>(arguments, "query", "*"),
            "skill_read" or "skill_reference" => ToolArguments.Get<string>(arguments, "name", "*"),
            "subagent" => ToolArguments.Get<string>(arguments, "agent", "general"),
            "cancel_agent" => ToolArguments.Get<string>(arguments, "id", "*"),
            "mcp_resource_read" => ToolArguments.Get<string>(arguments, "uri", "*"),
            "mcp_resources" => ToolArguments.Get<string>(arguments, "server_id", "*"),
            _ => "*"
        };
        var matched = current.Permissions.LastOrDefault(rule => (Match(action, rule.Action) || Match(tool.Name, rule.Action)) && Match(resource, rule.Resource));
        return (current.Name, matched?.Effect ?? "ask", JsonSerializer.Serialize(current));
    }

    // OpenCode normalizes slashes, supports * and ?, and treats a trailing " *" as optional arguments.
    internal static bool Match(string input, string pattern)
    {
        var escaped = Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", ".*").Replace("\\?", ".");
        const string optionalArguments = "\\ .*";
        if (escaped.EndsWith(optionalArguments, StringComparison.Ordinal)) escaped = escaped[..^optionalArguments.Length] + "( .*)?";
        return Regex.IsMatch(input.Replace('\\', '/'), "\\A" + escaped + "\\z", RegexOptions.Singleline | RegexOptions.NonBacktracking | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
    }
}
