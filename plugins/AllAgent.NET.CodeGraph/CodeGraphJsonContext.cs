using System.Text.Json.Serialization;

namespace AllAgent.NET.CodeGraph;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CodeIndex))]
[JsonSerializable(typeof(CodeGraphStatus))]
[JsonSerializable(typeof(CodeGraphOperation))]
[JsonSerializable(typeof(CodeGraphSettings))]
internal sealed partial class CodeGraphJsonContext : JsonSerializerContext;
