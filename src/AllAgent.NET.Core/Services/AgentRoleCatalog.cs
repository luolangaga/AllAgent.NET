using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Data;

namespace AllAgent.NET.Core.Services;

public sealed record AgentPermissionRule(string Action, string Resource, string Effect);
public sealed record AgentRoleDefinition(string Name, string Description, string Instructions, bool ReadOnly = false, string? Model = null, string? Provider = null,
    IReadOnlyList<AgentPermissionRule>? Permissions = null, IReadOnlyList<string>? Tools = null,
    SubAgentExecutionSettings? Execution = null);

/// <summary>Persisted role instructions and defaults; roles cannot extend the parent's permissions.</summary>
public sealed class AgentRoleCatalog(DataStore store)
{
    private const string Key = "agents:roles";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly AgentRoleDefinition[] Builtins = [
        new("general", "Implementation and research", "You are General: implement and research the delegated task using the parent's host permission mode."),
        new("explore", "Read-only codebase search and analysis", "You are Explore: search and analyze only. Return absolute file paths and evidence. Do not create, modify, delete, move or copy files.", true),
        new("investigate", "调查：定位问题、查找引用、复现线索，返回证据和修改建议", "Investigate the assigned question. Search relevant source paths first, then read the necessary context. Return findings with exact paths and lines, distinguish evidence from hypotheses, and identify the smallest proposed fix. Do not edit files or run external commands. Return missing information to the parent; do not ask the user.", true,
            Tools: ["file_read", "file_search", "list_directory", "web_search", "web_fetch", "skill_read", "skill_reference"]),
        new("modify", "修改：按指定范围实现修复，保留无关改动并报告变更", "Implement the delegated change within the explicitly assigned file scope. Read current content first, preserve unrelated edits, make complete changes, and inspect the result. Do not expand the objective or publish/commit. Report modified files, verification performed and remaining blockers to the parent. Do not ask the user or delegate.",
            Tools: ["file_read", "file_search", "list_directory", "file_write", "file_edit", "run_shell", "shell_status", "cancel_shell", "skill_read", "skill_reference"]),
        new("review", "审查：检查正确性、回归和边界条件，只报告有证据的问题", "Review the supplied change and relevant current files. Focus on concrete correctness, regressions and boundary failures. Return only actionable findings with paths, lines, severity, trigger and impact; distinguish unverified concerns. Use the diff provided by the parent or ask the parent to supply it in your result. Do not edit files, run external commands, ask the user or delegate.", true,
            Tools: ["file_read", "file_search", "list_directory", "skill_read", "skill_reference"]),
        new("verify", "验证：运行构建、测试或复现步骤，报告实际结果与失败原因", "Verify the assigned acceptance criteria with the documented checks. Do not edit source files or fix failures; report command, exit code, observed result, environment blockers and unverified behavior to the parent. Commands may create ordinary build/test artifacts. Use run_shell with exe and separate literal args for checks, avoiding shell command strings. Do not ask the user, delegate, suppress diagnostics or claim checks you did not run.",
            Tools: ["file_read", "file_search", "list_directory", "run_shell", "shell_status", "cancel_shell", "skill_read", "skill_reference"])
    ];
    private readonly object _sync = new();
    private string? _loaded;
    private AgentRoleDefinition[]? _cache;
    public IReadOnlyList<AgentRoleDefinition> List()
    {
        lock (_sync)
        {
            var raw = store.GetSetting(Key);
            if (_cache == null || raw != _loaded)
            {
                var overrides = Read(raw);
                _cache = Builtins.Concat(overrides).GroupBy(role => role.Name, StringComparer.Ordinal).Select(group => group.Last()).OrderBy(role => role.Name).ToArray();
                _loaded = raw;
            }
            return _cache.ToArray();
        }
    }
    public AgentRoleDefinition Get(string name)
    {
        var role = List().SingleOrDefault(role => role.Name == name) ?? throw new ArgumentException("Unknown agent role: " + name);
        PromptComposer.NoteSource("role:" + name, "agents:roles/" + name, role.Instructions);
        return role;
    }
    public static void Validate(string json) => Read(json);
    public void Import(string json)
    {
        var incoming = Read(json);
        lock (_sync)
        {
            var merged = Read(store.GetSetting(Key)).Concat(incoming).GroupBy(role => role.Name, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
            if (merged.Length > 64) throw new ArgumentException("最多配置 64 个 Agent 角色。");
            store.SetSetting(Key, JsonSerializer.Serialize(merged, Json));
        }
    }
    public void Remove(string name)
    {
        lock (_sync)
        {
            Get(name);
            store.SetSetting(Key, JsonSerializer.Serialize(Read(store.GetSetting(Key)).Where(role => role.Name != name), Json));
        }
    }
    private static AgentRoleDefinition[] Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        if (json.Length > 2_000_000) throw new ArgumentException("Agent 角色配置过大。");
        var roles = JsonSerializer.Deserialize<AgentRoleDefinition[]>(json, Json) ?? throw new ArgumentException("Agent 角色配置必须为数组。");
        if (roles.Length > 64 || roles.Any(role => role == null) || roles.Select(role => role.Name).Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new ArgumentException("Agent 角色数量超限或名称重复。");
        foreach (var role in roles)
        {
            role.Execution?.Validate();
            if (role.Name == null || !Regex.IsMatch(role.Name, "^[a-z][a-z0-9_-]{0,63}\\z") || string.IsNullOrWhiteSpace(role.Description) || role.Description.Length > 120 || string.IsNullOrWhiteSpace(role.Instructions) || role.Instructions.Length > 32000 || role.Model?.Length > 256 || role.Provider?.Length > 256)
                throw new ArgumentException("Agent 名称、说明、指令或模型配置无效。");
            if (role.Name is "explore" or "investigate" or "review" && !role.ReadOnly) throw new ArgumentException("调查与审查角色的只读限制不能通过配置取消。");
            if (role.Tools is { } allowed && (allowed.Count > 256 || allowed.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256) || allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Count))
                throw new ArgumentException("Agent tools must contain distinct nonempty names, at most 256.");
            if (role.Permissions is { } rules && (rules.Count > 256 || rules.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.Action) || rule.Action.Length > 256 || string.IsNullOrWhiteSpace(rule.Resource) || rule.Resource.Length > 4096 || rule.Effect is not ("allow" or "deny" or "ask"))))
                throw new ArgumentException("Agent 权限规则必须包含 action、resource 和 allow/deny/ask effect，最多 256 条。");
        }
        return roles.Select(role => role with { Execution = role.Execution == null ? null : role.Execution with { Skills = role.Execution.Skills == null ? null : Array.AsReadOnly(role.Execution.Skills.ToArray()) }, Permissions = role.Permissions == null ? null : Array.AsReadOnly(role.Permissions.ToArray()), Tools = role.Tools == null ? null : Array.AsReadOnly(role.Tools.ToArray()) }).ToArray();
    }
}
