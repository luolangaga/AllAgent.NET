using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Data;

namespace LanAgent.Core.Services;

public sealed record AgentPermissionRule(string Action, string Resource, string Effect);
public sealed record AgentRoleDefinition(string Name, string Description, string Instructions, bool ReadOnly = false, string? Model = null, string? Provider = null,
    IReadOnlyList<AgentPermissionRule>? Permissions = null);

/// <summary>Persisted role instructions and defaults; roles cannot extend the parent's permissions.</summary>
public sealed class AgentRoleCatalog(DataStore store)
{
    private const string Key = "agents:roles";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly AgentRoleDefinition[] Builtins = [
        new("general", "Implementation and research", "You are General: implement and research the delegated task using the parent's host permission mode."),
        new("explore", "Read-only codebase search and analysis", "You are Explore: search and analyze only. Return absolute file paths and evidence. Do not create, modify, delete, move or copy files.", true)
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
    public AgentRoleDefinition Get(string name) => List().SingleOrDefault(role => role.Name == name) ?? throw new ArgumentException("Unknown agent role: " + name);
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
            if (role.Name == null || !Regex.IsMatch(role.Name, "^[a-z][a-z0-9_-]{0,63}\\z") || string.IsNullOrWhiteSpace(role.Description) || role.Description.Length > 120 || string.IsNullOrWhiteSpace(role.Instructions) || role.Instructions.Length > 32000 || role.Model?.Length > 256 || role.Provider?.Length > 256)
                throw new ArgumentException("Agent 名称、说明、指令或模型配置无效。");
            if (role.Name == "explore" && !role.ReadOnly) throw new ArgumentException("Explore 的只读限制不能通过角色配置取消。");
            if (role.Permissions is { } rules && (rules.Count > 256 || rules.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.Action) || rule.Action.Length > 256 || string.IsNullOrWhiteSpace(rule.Resource) || rule.Resource.Length > 4096 || rule.Effect is not ("allow" or "deny" or "ask"))))
                throw new ArgumentException("Agent 权限规则必须包含 action、resource 和 allow/deny/ask effect，最多 256 条。");
        }
        return roles.Select(role => role with { Permissions = role.Permissions == null ? null : Array.AsReadOnly(role.Permissions.ToArray()) }).ToArray();
    }
}
