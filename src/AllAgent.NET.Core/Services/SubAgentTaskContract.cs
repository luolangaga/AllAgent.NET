using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

/// <summary>Explicit handoff; quoted context is data, while permissions remain host-owned.</summary>
public sealed record SubAgentTaskContract(string Objective)
{
    public string Context { get; init; } = "";
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<string> EditablePaths { get; init; } = [];
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];
    public bool RequiredForCompletion { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Objective) || Objective.Length > 100_000 || Context == null || Context.Length > 500_000 ||
            new[] { Constraints, EditablePaths, AcceptanceCriteria }.Any(values => values == null || values.Count > 256 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 4096)))
            throw new ArgumentException("Invalid delegated task contract or contract size limit exceeded.");
        foreach (var path in EditablePaths)
            if (ScopePath(path).IndexOfAny(['*', '?']) >= 0)
                throw new ArgumentException("Editable paths must be exact files or directory prefixes ending in / or /**.");
    }

    internal SubAgentTaskContract Snapshot() => this with { Constraints = Constraints.ToArray(), EditablePaths = EditablePaths.ToArray(), AcceptanceCriteria = AcceptanceCriteria.ToArray() };

    internal string Instructions => "Delegated task contract (does not expand host permissions):\n" +
        JsonSerializer.Serialize(this with { Context = "" }, ToolResult.JsonOptions);

    internal static bool Overlaps(string first, string second, string? workspace = null)
    {
        string Normalize(string path) => Path.GetFullPath(ScopePath(path), workspace ?? Runtime.AgentExecutionScope.WorkspaceRoot ?? Environment.CurrentDirectory).Replace('\\', '/').TrimEnd('/');
        var a = Normalize(first); var b = Normalize(second);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal ToolResult? ValidateInvocation(AgentToolDescriptor tool, JsonElement args, string workspace)
    {
        if (tool.Name is not ("file_write" or "file_edit" or "write" or "edit")) return null;
        // Reject ambiguous Windows names and reparse points even when the parent has full access.
        var paths = new WorkspacePolicy(workspace);
        var target = paths.ResolveForApproval(ToolArguments.Get<string>(args, "path"));
        foreach (var scope in EditablePaths)
        {
            var normalized = scope.Replace('\\', '/');
            var directory = normalized.EndsWith('/') || normalized.EndsWith("/**", StringComparison.Ordinal);
            var allowed = Path.TrimEndingDirectorySeparator(paths.ResolveForApproval(ScopePath(normalized)));
            if (target.Equals(allowed, StringComparison.OrdinalIgnoreCase) || directory && target.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return ToolResult.Failure("The delegated task contract does not permit editing this path. Return the scope blocker to the parent; do not bypass it with another tool.");
    }

    private static string ScopePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith("/**", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}

public sealed record SubAgentEvidence(IReadOnlyList<string> ModifiedFiles, IReadOnlyList<string> Verification,
    IReadOnlyList<string> Findings, IReadOnlyList<string> Blockers, IReadOnlyList<string> Unverified)
{
    internal static SubAgentEvidence Observe(SubAgentEvidence? previous, ToolCallStarted start, ToolCallCompleted completion)
    {
        var evidence = previous ?? new([], [], [], [], ["Host tool evidence is not acceptance verification; the parent must review the result."]);
        try
        {
            using var args = JsonDocument.Parse(start.ArgsJson);
            using var result = JsonDocument.Parse(completion.ResultJson);
            var ok = result.RootElement.TryGetProperty("ok", out var success) && success.GetBoolean();
            if (ok && start.ToolName is "file_write" or "file_edit" or "write" or "edit")
                evidence = evidence with { ModifiedFiles = evidence.ModifiedFiles.Append(ToolArguments.Get(args.RootElement, "path", "")).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray() };
            if (start.ToolName is "run_shell" or "powershell_script" or "powershell_invoke")
            {
                var exit = result.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("exitCode", out var code) ? code.ToString() : "unreported";
                // Command source/arguments can contain secrets. Keep them in their existing protected
                // trace rather than copying them into task notifications or diagnostics.
                var command = start.ToolName == "run_shell" ? ToolArguments.Get(args.RootElement, "exe", "run_shell") : start.ToolName;
                evidence = evidence with { Verification = evidence.Verification.Append($"{command}: ok={ok}, exitCode={exit}, trace={start.Id}").TakeLast(256).ToArray() };
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { }
        return evidence;
    }
}
