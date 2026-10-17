using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Tools;

public partial class PowerShellInvokeTools(WorkspacePolicy workspace, RuntimeEnvironment environment, PowerShellModuleStore store,
    WorkspaceCheckpoints? checkpoints = null, ExternalDirectoryPermissions? permissions = null) : IAgentToolPreflight, IAgentToolAccess
{
    public ToolResult? ValidateInvocation(string name, JsonElement arguments)
    {
        var module = ToolArguments.Get<string>(arguments, "module");
        var command = ToolArguments.Get<string>(arguments, "command");
        if (!Regex.IsMatch(module, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$") || !Regex.IsMatch(command, @"^[A-Za-z][A-Za-z0-9-]{0,127}$")) return ToolResult.Failure("Use exact module and exported command names, not scripts or paths.");
        if (!store.HasCommand(module, command)) return ToolResult.Failure("Command is not in the verified module catalog. Inspect powershell_capabilities first.");
        workspace.ResolveForApproval(ToolArguments.Get(arguments, "working_directory", "."));
        if (ToolArguments.Get(arguments, "timeout_seconds", 60) < 0) return ToolResult.Failure("timeout_seconds must be nonnegative.");
        if (arguments.TryGetProperty("parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object || parameters.GetRawText().Length > 100_000) return ToolResult.Failure("parameters must be a JSON object under 100,000 characters.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters.EnumerateObject())
                if (!Regex.IsMatch(parameter.Name, @"^[A-Za-z][A-Za-z0-9_]{0,127}$") || !names.Add(parameter.Name)) return ToolResult.Failure("Invalid or duplicate parameter name.");
        }
        return null;
    }
    public ValueTask<ToolAccessGrant?> RequestAccessAsync(string name, JsonElement arguments, ToolApprover? approver, CancellationToken ct) =>
        new SystemTools(workspace, environment, new Dictionary<string, string>(), permissions: permissions).RequestAccessAsync("run_shell", arguments, approver, ct);

    [AgentTool("powershell_invoke", Risk = ToolRisk.Execute), Description("Invoke one verified module command via AddCommand/AddParameter with JSON values. No script expressions; strings remain literal. Returns bounded JSON records. Requires execution approval; does not provide filesystem isolation.")]
    public Task<ToolResult> InvokeCommandAsync([Description("Exact installed module name")] string module, [Description("Exact exported command name")] string command,
        [Description("Named parameter object: strings, numbers, booleans, arrays, objects or null; switch parameters use booleans")] JsonElement parameters,
        [Description("Working directory, default workspace")] string working_directory = ".", [Description("Timeout seconds; zero is unlimited")] int timeout_seconds = 60, CancellationToken ct = default) =>
        RunAsync("--invoke", module, command, parameters, working_directory, timeout_seconds, ct);

    [AgentTool("powershell_command_info", Risk = ToolRisk.Execute), Description("Inspect real parameter names, types and parameter sets for a verified PowerShell command. Requires approval because discovery may import module code.")]
    public Task<ToolResult> CommandInfoAsync([Description("Exact installed module name")] string module, [Description("Exact exported command name")] string command,
        [Description("Working directory")] string working_directory = ".", CancellationToken ct = default) =>
        RunAsync("--command-info", module, command, JsonSerializer.SerializeToElement(new { }), working_directory, 60, ct);

    private async Task<ToolResult> RunAsync(string mode, string module, string command, JsonElement parameters, string directory, int seconds, CancellationToken ct)
    {
        var error = ValidateInvocation(mode, JsonSerializer.SerializeToElement(new { module, command, parameters, working_directory = directory, timeout_seconds = seconds }));
        if (error != null) return error;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { module, command, parameters })));
        var info = BoundedProcess.StartInfo(environment.PowerShell!, [mode, payload], workspace.Resolve(directory));
        info.Environment["ALLAGENT_PS_MODULE_ROOT"] = store.Root;
        info.RedirectStandardInput = true;
        checkpoints?.MarkExternalAction();
        var result = await BoundedProcess.RunAsync(info, seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds), ct, stripAnsi: true,
            tailOutputDirectory: workspace.Resolve(Path.Combine(workspace.Root, ".allagent", "tool-output")));
        var records = new List<JsonElement>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            try { records.Add(JsonSerializer.Deserialize<JsonElement>(line)); } catch (JsonException) { }
        return new(result.ExitCode == 0 && !result.TimedOut, new { records, exitCode = result.ExitCode, stderr = result.Stderr, timedOut = result.TimedOut, stdoutPath = result.StdoutPath, stderrPath = result.StderrPath,
            notice = "Records are bounded projections (depth 5, 64 properties, 100 array entries, 4000 characters per string); large output has full log paths." },
            result.TimedOut ? "PowerShell command timed out." : result.ExitCode == 0 ? null : "PowerShell command failed.", result.Truncated);
    }
}
