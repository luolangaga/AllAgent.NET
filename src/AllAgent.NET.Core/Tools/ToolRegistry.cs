using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AllAgent.NET.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, (IAgentToolSet Set, AgentToolDescriptor Definition)> _tools = new(StringComparer.Ordinal);
    private readonly ToolApprover? _approver;
    private readonly string? _auditPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly int _maxResultCharacters;
    private readonly int _maxStreamCharacters;
    private readonly object _sync = new();
    private readonly AllAgent.NET.Core.Extensions.HookPipeline? _hooks;
    private readonly Func<AgentToolDescriptor, JsonElement, ToolResult?>? _invocationPolicy;
    private readonly Func<AgentToolDescriptor, JsonElement, CancellationToken, ValueTask<ToolResult?>>? _invocationAuthorization;

    public ToolRegistry(ToolApprover? approver = null, string? auditPath = null, AllAgent.NET.Core.Extensions.HookPipeline? hooks = null,
        Func<AgentToolDescriptor, JsonElement, ToolResult?>? invocationPolicy = null,
        Func<AgentToolDescriptor, JsonElement, CancellationToken, ValueTask<ToolResult?>>? invocationAuthorization = null, int maxResultCharacters = 64_000, int maxStreamCharacters = 20_000)
    {
        if (maxResultCharacters < 4096 || maxResultCharacters > 4_000_000 || maxStreamCharacters < 1 || maxStreamCharacters > maxResultCharacters) throw new ArgumentOutOfRangeException(nameof(maxResultCharacters));
        _maxResultCharacters = maxResultCharacters; _maxStreamCharacters = maxStreamCharacters; _approver = approver; _auditPath = auditPath; _hooks = hooks; _invocationPolicy = invocationPolicy; _invocationAuthorization = invocationAuthorization;
    }
    public IReadOnlyList<AgentToolDescriptor> Definitions { get { lock (_sync) return _tools.Values.Select(t => t.Definition).Where(t => AllAgent.NET.Core.Extensions.AgentExtensionScope.Current?.Allows(this, t.Name) != false).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(); } }
    public void Register(IAgentToolSet tools)
    {
        lock (_sync)
        {
            var definitions = tools.Definitions.ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitions)
                if (!names.Add(definition.Name) || _tools.ContainsKey(definition.Name)) throw new InvalidOperationException($"Duplicate tool: {definition.Name}");
            foreach (var definition in definitions) _tools.Add(definition.Name, (tools, definition));
        }
    }
    public void Replace<T>(IAgentToolSet tools, bool preserveOtherToolSets = false) where T : IAgentToolSet
    {
        lock (_sync)
        {
            if (tools is not T) throw new ArgumentException("Replacement must have the selected tool-set type.");
            var definitions = tools.Definitions.ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitions)
                if (!names.Add(definition.Name) || !preserveOtherToolSets && _tools.TryGetValue(definition.Name, out var existing) && existing.Set is not T)
                    throw new InvalidOperationException($"Duplicate tool: {definition.Name}");
            foreach (var key in _tools.Where(pair => pair.Value.Set is T).Select(pair => pair.Key).ToArray()) _tools.Remove(key);
            foreach (var definition in definitions)
                if (!_tools.ContainsKey(definition.Name)) _tools.Add(definition.Name, (tools, definition));
        }
    }

    public async ValueTask<ToolResult> CallAsync(string name, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        ToolResult result;
        try
        {
            (IAgentToolSet Set, AgentToolDescriptor Definition) entry;
            lock (_sync) { if (AllAgent.NET.Core.Extensions.AgentExtensionScope.Current?.Allows(this, name) == false || !_tools.TryGetValue(name, out entry)) return ToolResult.Failure("Unknown or disabled tool."); }
            if (entry.Set is IAgentToolArgumentPreparer preparer) args = await preparer.PrepareArgumentsAsync(name, args, ct);
            try { ToolArguments.Validate(args, entry.Definition.Parameters); }
            catch (ArgumentException ex)
            {
                var properties = entry.Definition.Parameters.TryGetProperty("properties", out var fields)
                    ? string.Join(", ", fields.EnumerateObject().Select(field => field.Name + ":" + (field.Value.TryGetProperty("type", out var type) ? type.ToString() : "schema"))) : "see catalog";
                throw new ArgumentException($"Invalid arguments for {name}: {ex.Message} Expected named fields: {properties}.");
            }
            var preflight = _invocationPolicy?.Invoke(entry.Definition, args) ?? (entry.Set as IAgentToolPreflight)?.ValidateInvocation(name, args);
            if (preflight != null) result = preflight;
            else
            {
            var grant = entry.Set is IAgentToolAccess access ? await access.RequestAccessAsync(name, args, _approver, ct) : null;
            using var lease = grant?.Enter();
            if (_hooks != null) await _hooks.NotifyAsync(new(AllAgent.NET.Core.Extensions.HookStage.BeforeTool, ToolName: name, Arguments: args.Clone()), ct);
            var approved = true;
            if (entry.Definition.Risk != ToolRisk.Read)
            {
                var approvalPolicy = (entry.Set as IAgentToolApprovalPolicy)?.GetApprovalPolicy(name, args) ?? new ToolApprovalPolicy();
                var approvalRequest = new ToolApprovalRequest(name, entry.Definition.Risk, args.Clone())
                {
                    ExternalPathApproved = grant != null,
                    Preapproved = !approvalPolicy.RequiresApproval,
                    ResolvedTarget = approvalPolicy.ResolvedTarget
                };
                approved = _approver != null
                    ? await _approver(approvalRequest, ct)
                    : approvalRequest.Preapproved;
            }
            if (entry.Definition.Risk != ToolRisk.Read && !approved)
                result = ToolResult.Failure("Tool approval denied. Do not retry or use another tool to bypass this decision.");
            else
            {
                await AuditAsync(id, name, args, "started", null, ct);
                var authorization = _invocationAuthorization == null ? null : await _invocationAuthorization(entry.Definition, args, ct);
                ct.ThrowIfCancellationRequested();
                result = authorization ?? _invocationPolicy?.Invoke(entry.Definition, args) ?? await entry.Set.InvokeAsync(name, args, ct);
            }
            }
        }
        catch (OperationCanceledException) { await AuditAsync(id, name, args, "cancelled", false, CancellationToken.None); throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { result = ToolResult.Failure(ex.Message); }
        result = LimitResult(result);
        await AuditAsync(id, name, args, "completed", result.Ok, ct);
        if (_hooks != null) await _hooks.NotifyAsync(new(AllAgent.NET.Core.Extensions.HookStage.AfterTool, ToolName: name, Ok: result.Ok), ct);
        return result;
    }

    internal ToolResult LimitResult(ToolResult result) => ToolResultBudget.Apply(result, _maxResultCharacters, _maxStreamCharacters);

    private async Task AuditAsync(string id, string name, JsonElement args, string phase, bool? ok, CancellationToken ct)
    {
        if (_auditPath == null) return;
        // Retain operation identity without putting file content, credentials or shell arguments in logs.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(args.GetRawText())));
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, id, tool = name, argumentsHash = hash, phase, ok });
        await _auditLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_auditPath))!);
            await File.AppendAllTextAsync(_auditPath, line + Environment.NewLine, ct);
        }
        finally { _auditLock.Release(); }
    }
}
