using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record InvocationRequest(string module, string command, JsonElement parameters);

internal static class CommandInvocation
{
    internal static int Run(PowerShell shell, InvocationRequest request, bool describe)
    {
        if (request == null || !Regex.IsMatch(request.module, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$") || !Regex.IsMatch(request.command, @"^[A-Za-z][A-Za-z0-9-]{0,127}$")) throw new ArgumentException("Invalid module or command.");
        var qualified = request.module + "\\" + request.command;
        if (request.module != "Microsoft.PowerShell.Core")
        {
            shell.AddCommand("Microsoft.PowerShell.Core\\Import-Module").AddParameter("Name", request.module).AddParameter("Global", true).AddParameter("ErrorAction", ActionPreference.Stop);
            shell.Invoke();
            if (shell.HadErrors) throw new InvalidOperationException(string.Join("; ", shell.Streams.Error.Select(e => e.ToString())));
            shell.Commands.Clear();
        }
        var info = shell.Runspace.SessionStateProxy.InvokeCommand.GetCommand(qualified, CommandTypes.Cmdlet | CommandTypes.Function | CommandTypes.Filter | CommandTypes.Alias)
            ?? throw new ArgumentException("Exported module command not found: " + qualified);
        if (describe)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { module = request.module, command = info.Name,
                parameterSets = info.ParameterSets.Select(set => new { name = set.Name, isDefault = set.IsDefault, parameters = set.Parameters.Select(p => new { name = p.Name, type = p.ParameterType.FullName, mandatory = p.IsMandatory, position = p.Position, aliases = p.Aliases }) }) }));
            return 0;
        }
        if (request.parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("parameters must be an object.");
        shell.AddCommand(qualified);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in request.parameters.EnumerateObject())
        {
            if (!seen.Add(parameter.Name) || !info.Parameters.TryGetValue(parameter.Name, out var metadata)) throw new ArgumentException("Unknown or duplicate parameter: " + parameter.Name);
            var value = ConvertValue(parameter.Value);
            if (metadata.ParameterType == typeof(SwitchParameter)) value = parameter.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new SwitchParameter(parameter.Value.GetBoolean()) : throw new ArgumentException("Switch parameters require a boolean.");
            shell.AddParameter(parameter.Name, value);
        }
        shell.Runspace.SessionStateProxy.SetVariable("ErrorActionPreference", ActionPreference.Stop);
        using var output = new PSDataCollection<PSObject>();
        output.DataAdded += (_, _) => { foreach (var item in output.ReadAll()) Console.WriteLine(JsonSerializer.Serialize(Project(item, 0))); };
        shell.Streams.Error.DataAdded += (_, _) => { foreach (var item in shell.Streams.Error.ReadAll()) Console.Error.WriteLine(item); };
        shell.Streams.Warning.DataAdded += (_, _) => { foreach (var item in shell.Streams.Warning.ReadAll()) Console.Error.WriteLine(item.Message); };
        shell.Streams.Information.DataAdded += (_, _) => { foreach (var item in shell.Streams.Information.ReadAll()) Console.Error.WriteLine(item.MessageData); };
        using var input = new PSDataCollection<PSObject>(); input.Complete();
        shell.EndInvoke(shell.BeginInvoke<PSObject, PSObject>(input, output));
        return shell.HadErrors ? 1 : 0;
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var number) ? (object)number : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToArray(),
        JsonValueKind.Object => new Hashtable(value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value))),
        _ => throw new ArgumentException("Unsupported JSON parameter value.")
    };

    private static object? Project(object? value, int depth)
    {
        if (value is PSObject wrapper && wrapper.BaseObject is not PSCustomObject) value = wrapper.BaseObject;
        if (value == null || value is bool or byte or short or int or long or float or double or decimal) return value;
        if (value is string text) return text[..Math.Min(text.Length, 4000)];
        if (depth >= 5 || value is DateTime or DateTimeOffset or Guid or Enum) return value.ToString();
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dictionary) { if (result.Count >= 64) break; result[item.Key.ToString()!] = Project(item.Value, depth + 1); }
            return result;
        }
        if (value is IEnumerable array) return array.Cast<object?>().Take(100).Select(item => Project(item, depth + 1)).ToArray();
        var properties = new Dictionary<string, object?>();
        foreach (var property in PSObject.AsPSObject(value).Properties.Take(64))
            try { properties[property.Name] = Project(property.Value, depth + 1); } catch { properties[property.Name] = null; }
        return properties;
    }
}
