using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Extensions;

/// <summary>Runs once per user turn, before the sandbox and prompt are prepared.</summary>
public sealed record AgentTurnExtension(string Name, Func<AgentTurnContext, CancellationToken, ValueTask> Configure, int Order = 0);
/// <summary>Runs once per model step, before retries. Changes affect only the outgoing request.</summary>
public sealed record AgentRequestExtension(string Name, Func<AgentRequestContext, CancellationToken, ValueTask> Configure, int Order = 0);

public sealed class AgentTurnContext(Guid sessionId, string workspaceRoot, bool isSubAgent, string userMessage,
    string systemPrompt, IEnumerable<string> tools, IEnumerable<InstructionSkill> skills)
{
    public Guid SessionId { get; } = sessionId;
    public string WorkspaceRoot { get; } = workspaceRoot;
    public bool IsSubAgent { get; } = isSubAgent;
    public string UserMessage { get; } = userMessage;
    /// <summary>Session system prompt. Project instructions and the CodeAct contract are assembled separately.</summary>
    public string SystemPrompt { get; set; } = systemPrompt;
    /// <summary>Remove registered names to disable them for this turn. Add tools with PluginContext.AddTools.</summary>
    public HashSet<string> Tools { get; } = new(tools, StringComparer.Ordinal);
    /// <summary>Remove, replace or add instruction skills without writing the persistent catalog.</summary>
    public Dictionary<string, InstructionSkill> Skills { get; } = skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    internal AgentTurnContext Copy() => new(SessionId, WorkspaceRoot, IsSubAgent, UserMessage, SystemPrompt, Tools, Skills.Values);
}

public sealed class AgentRequestContext(Guid sessionId, bool isSubAgent, int iteration, IEnumerable<ChatMessage> messages)
{
    public Guid SessionId { get; } = sessionId;
    public bool IsSubAgent { get; } = isSubAgent;
    public int Iteration { get; } = iteration;
    /// <summary>May edit any outgoing system/context message. Persistent history is not rewritten.</summary>
    public List<ChatMessage> Messages { get; } = messages.ToList();
    internal AgentRequestContext Copy() => new(SessionId, IsSubAgent, Iteration, Messages);
}

public sealed class AgentExtensionPipeline(Func<IReadOnlyList<AgentTurnExtension>> turns, Func<IReadOnlyList<AgentRequestExtension>> requests)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public async Task<AgentTurnContext> ConfigureTurnAsync(AgentTurnContext context, CancellationToken ct)
    {
        var registered = context.Tools.ToHashSet(StringComparer.Ordinal);
        foreach (var extension in turns().OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var copy = context.Copy();
            await InvokeAsync(extension.Name, token => extension.Configure(copy, token), ct);
            if (copy.SystemPrompt == null || !copy.Tools.IsSubsetOf(registered)) throw new InvalidOperationException("插件不能启用未注册的工具：" + extension.Name);
            foreach (var (name, skill) in copy.Skills)
            {
                SkillCatalog.ValidateName(name);
                if (skill == null || !name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(skill.Instructions) || skill.Instructions.Length > 500000 || string.IsNullOrWhiteSpace(skill.Description))
                    throw new InvalidOperationException("插件技能无效：" + extension.Name + "/" + name);
            }
            context = copy;
        }
        return context;
    }
    public async Task<AgentRequestContext> ConfigureRequestAsync(AgentRequestContext context, CancellationToken ct)
    {
        foreach (var extension in requests().OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var copy = context.Copy();
            await InvokeAsync(extension.Name, token => extension.Configure(copy, token), ct);
            if (copy.Messages.Count == 0 || copy.Messages.Any(m => m == null || m.Content == null)) throw new InvalidOperationException("插件生成空的模型上下文：" + extension.Name);
            context = copy;
        }
        return context;
    }
    private async Task InvokeAsync(string name, Func<CancellationToken, ValueTask> callback, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try { await Task.Run(() => callback(deadline.Token).AsTask(), CancellationToken.None).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("插件扩展超时：" + name); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException("插件扩展失败：" + name + "：" + ex.Message, ex); }
    }
}

/// <summary>Immutable per-turn selection, isolated across concurrent runs sharing a registry/catalog.</summary>
internal sealed class AgentExtensionScope : IDisposable
{
    private static readonly AsyncLocal<AgentExtensionScope?> Slot = new();
    internal static AgentExtensionScope? Current => Slot.Value;
    private readonly AgentExtensionScope? _previous;
    private readonly ToolRegistry _registry;
    private readonly SkillCatalog? _catalog;
    private readonly HashSet<string> _tools;
    private readonly InstructionSkill[] _skills;
    internal AgentExtensionScope(AgentTurnContext context, ToolRegistry registry, SkillCatalog? catalog)
    {
        _registry = registry; _catalog = catalog;
        _tools = new(context.Tools, StringComparer.Ordinal); _skills = context.Skills.Values.ToArray();
        _previous = Slot.Value; Slot.Value = this;
    }
    internal bool Allows(ToolRegistry registry, string name) => !ReferenceEquals(registry, _registry) || _tools.Contains(name);
    internal IReadOnlyList<InstructionSkill> SelectSkills(SkillCatalog catalog, IReadOnlyList<InstructionSkill> fallback) => ReferenceEquals(catalog, _catalog) ? _skills : fallback;
    public void Dispose() => Slot.Value = _previous;
}
