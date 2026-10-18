using System.Text.Json;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.CodeAct;

/// <summary>Embedded capability list shared by the host validator and its tests.</summary>
internal static class PythonCodePolicy
{
    internal static JsonElement Definition { get; } = JsonSerializer.Deserialize<JsonElement>(RuntimeScripts.Read("code_policy.json"));
}
