using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

/// <summary>Legacy skill API. Executable templates are no longer trusted execution boundaries.</summary>
public sealed class SkillExecutor
{
    private readonly ToolRegistry? _registry;
    public SkillExecutor(ToolRegistry? registry = null) => _registry = registry;

    public async IAsyncEnumerable<string> ExecuteAsync(Skill skill, JsonElement arguments, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (skill.Type != SkillType.Builtin || skill.Name == "shell_exec")
        {
            yield return ToolResult.Failure("Legacy command strings and shell templates are disabled. Register a typed AgentTool and use run_shell(exe, args) through CodeAct.").ToJson();
            yield break;
        }
        if (_registry == null) { yield return ToolResult.Failure("A host ToolRegistry is required for legacy skill invocation.").ToJson(); yield break; }
        yield return (await _registry.CallAsync(skill.Name, arguments, ct)).ToJson();
    }
}
