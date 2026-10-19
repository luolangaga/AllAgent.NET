using System.Text.Json;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Tools;

public enum ToolRisk { Read, Write, Execute }

[AttributeUsage(AttributeTargets.Method)]
public sealed class AgentToolAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public ToolRisk Risk { get; set; }
}

public sealed record AgentToolDescriptor(string Name, string Description, JsonElement Parameters, ToolRisk Risk)
{
    public SkillDefinition ToDefinition() => new(Name, Description, Parameters);
}

public interface IAgentToolSet
{
    IReadOnlyList<AgentToolDescriptor> Definitions { get; }
    ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct);
}
public interface IAgentToolArgumentPreparer
{
    ValueTask<JsonElement> PrepareArgumentsAsync(string name, JsonElement arguments, CancellationToken ct);
}

/// <summary>Reject invalid host operations before asking users to approve them.</summary>
public interface IAgentToolPreflight
{
    ToolResult? ValidateInvocation(string name, JsonElement arguments);
}
public sealed class ToolAccessGrant(Func<IDisposable> enter)
{
    public IDisposable Enter() => enter();
}
public interface IAgentToolAccess
{
    ValueTask<ToolAccessGrant?> RequestAccessAsync(string name, JsonElement arguments, ToolApprover? approver, CancellationToken ct);
}

/// <summary>WorkspaceScoped marks an invocation whose only reachable effects are paths the host already
/// authorized for this call: inside the workspace, or outside it through the external-directory question
/// that already ran. Restricted mode runs those without a second prompt.</summary>
public sealed record ToolApprovalPolicy(bool RequiresApproval = true, string? ResolvedTarget = null, bool WorkspaceScoped = false);
public interface IAgentToolApprovalPolicy
{
    ToolApprovalPolicy GetApprovalPolicy(string name, JsonElement arguments);
}

public sealed record ToolOutputTruncation(int OriginalJsonCharacters, int RetainedJsonCharacters, int RemovedJsonCharacters);

public sealed record ToolResult(bool Ok, object? Data = null, string? Error = null, bool Truncated = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<AllAgent.NET.Core.Models.ImageAttachment> Images { get; init; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ToolOutputTruncation? OutputTruncation { get; init; }
    public const int MaxOutputCharacters = 20_000;
    public static ToolResult Success(object? data, bool truncated = false) => new(true, data, Truncated: truncated);
    public static ToolResult Failure(string error) => new(false, Error: Clip(error));
    public static string Clip(string? text, int limit = MaxOutputCharacters) => string.IsNullOrEmpty(text) ? "" : text[..Math.Min(text.Length, limit)];
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed record ToolApprovalRequest(string Name, ToolRisk Risk, JsonElement Arguments, string? Source = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public Action? RememberForSession { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Preapproved { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string? ResolvedTarget { get; init; }
    public bool ExternalPathApproved { get; init; }
    public bool WorkspaceScoped { get; init; }
    public bool RequiresExplicitApproval { get; init; }
}
public delegate ValueTask<bool> ToolApprover(ToolApprovalRequest request, CancellationToken ct);

public sealed class ToolArguments
{
    public static void Validate(JsonElement args, JsonElement schema)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be a JSON object.");
        if (!schema.TryGetProperty("properties", out var properties)) return;
        foreach (var property in args.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var definition))
            {
                if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    throw new ArgumentException($"Unknown argument: {property.Name}");
                continue;
            }
            ValidateValue(property.Value, definition, property.Name);
        }
        if (!schema.TryGetProperty("required", out var requiredList)) return;
        foreach (var required in requiredList.EnumerateArray())
            if (!args.TryGetProperty(required.GetString()!, out _))
                throw new ArgumentException($"Missing argument: {required.GetString()}");
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string name)
    {
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
        var expected = type.GetString();
        var valid = expected switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
        if (!valid) throw new ArgumentException($"Invalid type for {name}; expected {expected}.");
        if (expected == "object") Validate(value, schema);
        if (expected == "array" && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) ValidateValue(item, items, name + "[]");
    }

    public static T Get<T>(JsonElement args, string name, T fallback = default!) =>
        args.TryGetProperty(name, out var value) ? value.Deserialize<T>(ToolResult.JsonOptions)! : fallback;
}
