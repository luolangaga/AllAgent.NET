namespace AllAgent.NET.Core.Tools;

using AllAgent.NET.Core.Services;

/// <summary>
/// Session-scoped record of on-demand tool capabilities. Every host tool stays registered so a call
/// can still be answered, audited and approved; this type only decides what the model sees in the
/// catalog and whether a gated call may run yet. Hosts keep one instance per conversation, because a
/// runtime (and its provider) is rebuilt for every turn and an activation must outlive it.
/// </summary>
public sealed class ToolSurface
{
    /// <param name="Skill">Enabled instruction skill that discloses the contract, or null when no skill can.</param>
    /// <param name="Patterns">Exact tool names or prefixes ending in '*'.</param>
    /// <param name="Hint">Skill that is expected to own the family, so a pointer can still name it while the skill is disabled.</param>
    public sealed record Capability(string? Skill, IReadOnlyList<string> Patterns, string? Hint = null)
    {
        public string Label => Skill ?? Hint ?? string.Join(", ", Patterns);
        public bool SameAs(Capability other) =>
            string.Equals(Skill, other.Skill, StringComparison.Ordinal) && string.Equals(Hint, other.Hint, StringComparison.Ordinal) &&
            Patterns.SequenceEqual(other.Patterns, StringComparer.Ordinal);
    }

    private readonly object _sync = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private Capability[] _capabilities = [];
    private long _version = 1;

    /// <summary>Changes whenever the declaration or an activation changes; prompts cache on it.</summary>
    public long Version { get { lock (_sync) return _version; } }

    public IReadOnlyList<Capability> Capabilities { get { lock (_sync) return _capabilities.ToArray(); } }

    public IReadOnlyList<string> ActiveSkills { get { lock (_sync) return _active.Order(StringComparer.Ordinal).ToArray(); } }

    public static bool Matches(string pattern, string tool) =>
        pattern.EndsWith('*')
            ? tool.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)
            : string.Equals(pattern, tool, StringComparison.Ordinal);

    public static bool Matches(Capability capability, string tool) => capability.Patterns.Any(pattern => Matches(pattern, tool));

    /// <summary>The capability that owns a tool, or null when the tool is always visible.</summary>
    public Capability? Match(string tool)
    {
        lock (_sync)
        {
            foreach (var capability in _capabilities) if (Matches(capability, tool)) return capability;
            return null;
        }
    }

    /// <summary>True when the tool is deliberately kept out of the catalog, whether or not it can still run.</summary>
    public bool IsHidden(string tool) => Match(tool) != null;

    public bool IsActive(string skill) { lock (_sync) return _active.Contains(skill); }

    /// <summary>True when the catalog may show the tool and a call may run right now.</summary>
    public bool IsAvailable(string tool)
    {
        var capability = Match(tool);
        if (capability == null) return true;
        return capability.Skill != null && IsActive(capability.Skill);
    }

    public Capability? CapabilityFor(string skill)
    {
        lock (_sync) return _capabilities.FirstOrDefault(capability => string.Equals(capability.Skill, skill, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mark the capability disclosed by a loaded skill; returns its patterns (empty when unknown).</summary>
    public IReadOnlyList<string> Activate(string skill)
    {
        lock (_sync)
        {
            var capability = _capabilities.FirstOrDefault(item => string.Equals(item.Skill, skill, StringComparison.OrdinalIgnoreCase));
            if (capability == null) return [];
            if (_active.Add(capability.Skill!)) _version++;
            return capability.Patterns.ToArray();
        }
    }

    public void Deactivate(string skill)
    {
        lock (_sync) if (_active.Remove(skill)) _version++;
    }

    /// <summary>Back to a fresh surface: nothing disclosed. Conversations start that way.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (_active.Count == 0) return;
            _active.Clear();
            _version++;
        }
    }

    /// <summary>Replace the declared capabilities; the version changes only when the declaration does.</summary>
    public void Sync(IEnumerable<Capability> capabilities)
    {
        var next = capabilities.ToArray();
        lock (_sync)
        {
            if (next.Length == _capabilities.Length && next.Select((capability, index) => capability.SameAs(_capabilities[index])).All(same => same)) return;
            _capabilities = next;
            // A capability that disappeared (skill disabled or removed) is no longer disclosed.
            _active.RemoveWhere(skill => !next.Any(capability => string.Equals(capability.Skill, skill, StringComparison.OrdinalIgnoreCase)));
            _version++;
        }
    }

    /// <summary>Skills own their declared tools; registered capability families without a declaring skill stay hidden.</summary>
    public static IReadOnlyList<Capability> Discover(SkillCatalog? catalog, IEnumerable<AgentToolDescriptor> tools)
    {
        var skills = catalog?.List() ?? [];
        var names = tools.Select(tool => tool.Name).ToArray();
        var declared = new List<Capability>();
        foreach (var skill in skills.Where(skill => skill.Enabled && skill.ModelInvocable && skill.DeclaredTools.Count > 0).OrderBy(skill => skill.Name, StringComparer.Ordinal))
            declared.Add(new(skill.Name, skill.DeclaredTools));
        // Built-ins keep working for installations whose SKILL.md predates the tools field: the tool
        // family is hidden either way, and it becomes loadable whenever its skill is enabled.
        foreach (var (name, patterns) in Builtins)
        {
            if (!patterns.Any(pattern => names.Any(tool => Matches(pattern, tool)))) continue;
            if (declared.Any(capability => capability.Patterns.Any(pattern => patterns.Contains(pattern, StringComparer.Ordinal)))) continue;
            var owner = skills.FirstOrDefault(skill => skill.Enabled && skill.ModelInvocable && skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
            declared.Add(new(owner, patterns, name));
        }
        return declared;
    }

    private static readonly (string Skill, string[] Patterns)[] Builtins =
    [
        ("computer-use", ["computer_*"]),
        ("browser-use", ["browser"]),
        ("code-graph", ["codegraph_*"])
    ];
}
