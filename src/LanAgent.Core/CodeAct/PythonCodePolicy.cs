using System.Text.Json;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.CodeAct;

/// <summary>The host validator and model guidance share the same embedded capability list.</summary>
internal static class PythonCodePolicy
{
    internal static JsonElement Definition { get; } = JsonSerializer.Deserialize<JsonElement>(RuntimeScripts.Read("code_policy.json"));

    internal static string Guidance { get; } =
        "LanAgent source validation allows only these built-in calls: " + List("functions") +
        ". Allowed public method names: " + List("methods") +
        ". You may define and call ordinary helper functions with public names; no decorators or classes. " +
        "Use isinstance(value, dict/list/str/etc.) for basic type checks; truthiness does not establish a type. " +
        "Strings support replace, split, splitlines, join and strip; no regex imports are needed for literal replacements. Batch host operations with for loops and check each result. " +
        "Imports, direct I/O, eval/exec/open, type/getattr/setattr/hasattr, private attributes and argument unpacking are not allowed. " +
        "try/except/else/finally is allowed for defensive pure logic; catch Exception, ValueError, TypeError, KeyError, IndexError, RuntimeError or ZeroDivisionError. Check host result ok fields because host failures are values, not exceptions. Call host tools with one literal name and explicit named arguments. Do not rebind built-ins or helper function names. " +
        "These are LanAgent host validation rules; they apply in addition to the sandbox runtime guidance.";

    private static string List(string property) => string.Join(", ", Definition.GetProperty(property).EnumerateArray().Select(item => item.GetString()));
}
