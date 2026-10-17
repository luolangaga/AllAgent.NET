using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Tools;

public partial class PowerShellScriptTools(WorkspacePolicy workspace, RuntimeEnvironment environment, PowerShellSessions sessions,
    WorkspaceCheckpoints? checkpoints = null, ExternalDirectoryPermissions? permissions = null) : IAgentToolPreflight, IAgentToolAccess
{
    public ToolResult? ValidateInvocation(string name, JsonElement arguments)
    {
        if (name != "powershell_script") return null;
        if (!environment.Ready) return ToolResult.Failure(
            $"Runtime environment checks failed: embedded PowerShell SDK={(environment.PowerShell == null ? "missing or unsupported" : "ready")}. Run doctor for diagnostics. " + string.Join("; ", environment.Problems));
        var script = ToolArguments.Get<string>(arguments, "script");
        if (string.IsNullOrWhiteSpace(script)) return ToolResult.Failure("powershell_script.script must be a non-empty PowerShell source string.");
        if (script.Length > 64_000) return ToolResult.Failure("powershell_script.script exceeds 64,000 characters; split the work or deliver a .ps1 file instead.");
        if (ToolArguments.Get(arguments, "output", "objects") is not ("objects" or "text" or "schema")) return ToolResult.Failure("powershell_script.output must be objects, text or schema.");
        if (!Regex.IsMatch(ToolArguments.Get(arguments, "session", "main"), @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")) return ToolResult.Failure("powershell_script.session must start with a letter or digit and use only letters, digits, dot, dash or underscore (max 64 characters).");
        if (ToolArguments.Get(arguments, "timeout_seconds", 120) < 0) return ToolResult.Failure("powershell_script.timeout_seconds must be nonnegative; 0 means no timeout.");
        workspace.ResolveForApproval(ToolArguments.Get(arguments, "working_directory", "."));
        return null;
    }

    public ValueTask<ToolAccessGrant?> RequestAccessAsync(string name, JsonElement arguments, ToolApprover? approver, CancellationToken ct) =>
        new SystemTools(workspace, environment, new Dictionary<string, string>(), permissions: permissions).RequestAccessAsync("run_shell", arguments, approver, ct);

    [AgentTool("powershell_script", Risk = ToolRisk.Execute), Description("Run PowerShell code in a persistent named session; variables, functions, imports and the working directory survive between calls. output: objects (default, bounded JSON projections), text or schema. Returns error/warning/verbose/debug/information/progress streams. Requires execution approval; host execution, not a sandbox.")]
    public async Task<ToolResult> RunScriptAsync(
        [Description("Complete PowerShell source; keep results small with Select-Object and reuse session variables")] string script,
        [Description("objects (bounded JSON projections, default), text or schema (types and members)")] string output = "objects",
        [Description("Named persistent session; default main")] string session = "main",
        [Description("Clear the session (variables, functions, imports) before running")] bool reset = false,
        [Description("Relative or absolute working directory; external paths require directory approval")] string working_directory = ".",
        [Description("Timeout in seconds; 0 means no timeout")] int timeout_seconds = 120,
        CancellationToken ct = default)
    {
        var rejected = ValidateInvocation("powershell_script", JsonSerializer.SerializeToElement(new { script, output, session, reset, working_directory, timeout_seconds }));
        if (rejected != null) return rejected;
        var directory = workspace.Resolve(working_directory);
        checkpoints?.MarkExternalAction();
        var outcome = await sessions.RunAsync(session, script, output, reset, directory, timeout_seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeout_seconds), ct);
        var data = outcome.Data.ValueKind == JsonValueKind.Object ? JsonNode.Parse(outcome.Data.GetRawText())!.AsObject() : new JsonObject();
        data["session"] = session;
        if (outcome.SessionRestarted) data["sessionRestarted"] = true;
        var truncated = data.TryGetPropertyValue("truncated", out var truncatedNode) && truncatedNode is JsonValue truncatedValue && truncatedValue.TryGetValue<bool>(out var isTruncated) && isTruncated;
        if (outcome.TimedOut)
        {
            data["timedOut"] = true;
            data["notice"] = "The session was reset; the next call starts a new runspace.";
            return new(false, data, outcome.Error ?? "PowerShell script timed out.");
        }
        if (!outcome.Ok) return new(false, data, outcome.Error ?? "PowerShell script failed.", truncated);
        if (truncated) data["notice"] = "Output truncated; narrow the pipeline with Select-Object or rerun a smaller query.";
        var success = data.TryGetPropertyValue("success", out var successNode) && successNode is JsonValue successValue && successValue.TryGetValue<bool>(out var succeeded) && succeeded;
        return new(success, data, success ? null : "PowerShell reported errors or a nonzero exit code.", truncated);
    }
}
