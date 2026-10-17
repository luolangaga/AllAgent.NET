using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal sealed record ServeRequest(long id, string kind, string? script, string? output, bool reset, string? workingDirectory);

/// <summary>Line-delimited JSON protocol over stdin/stdout; one persistent runspace per process.</summary>
internal static class ServeMode
{
    private const int MaxRecords = 512;
    private const int MaxPayloadCharacters = 160_000;
    private const int MaxStreamRecords = 32;
    private const int MaxProgressRecords = 8;
    private const int MaxStringCharacters = 4_000;
    private const int MaxSchemaTypes = 5;
    private const string Prologue = """
        $PSStyle.OutputRendering = 'PlainText'
        $PSNativeCommandArgumentPassing = 'Standard'
        Remove-Variable LASTEXITCODE -ErrorAction SilentlyContinue
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static int Run(RunspaceBootstrap bootstrap)
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        Environment.SetEnvironmentVariable("TERM", "dumb");
        Console.Out.WriteLine(JsonSerializer.Serialize(new { kind = "ready" }, JsonOptions));
        Console.Out.Flush();
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            line = line.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode frame;
            try { frame = Handle(bootstrap, line); }
            catch (Exception ex) { frame = new JsonObject { ["kind"] = "error", ["error"] = Shorten(ex.Message) }; }
            Console.Out.WriteLine(frame.ToJsonString(JsonOptions));
            Console.Out.Flush();
        }
        return 0;
    }

    private static JsonNode Handle(RunspaceBootstrap bootstrap, string line)
    {
        ServeRequest request;
        try { request = JsonSerializer.Deserialize<ServeRequest>(line, JsonOptions) ?? throw new ArgumentException("Empty request frame."); }
        catch (JsonException ex) { return new JsonObject { ["kind"] = "error", ["error"] = "Invalid request frame: " + ex.Message }; }
        if (request.kind != "run") return new JsonObject { ["kind"] = "error", ["id"] = request.id, ["error"] = "Unknown frame kind: " + request.kind };
        return RunScript(bootstrap, request);
    }

    private static JsonNode RunScript(RunspaceBootstrap bootstrap, ServeRequest request)
    {
        if (string.IsNullOrEmpty(request.script)) throw new ArgumentException("script is required.");
        var mode = request.output is "text" or "schema" ? request.output : "objects";
        if (request.reset) bootstrap.Reset();
        if (!string.IsNullOrEmpty(request.workingDirectory)) bootstrap.Runspace.SessionStateProxy.Path.SetLocation(request.workingDirectory);
        var stopwatch = Stopwatch.StartNew();
        PowerShell? shell = null;
        PSDataCollection<PSObject>? results = null;
        Exception? failure = null;
        try
        {
            using (var setup = PowerShell.Create())
            {
                setup.Runspace = bootstrap.Runspace;
                setup.AddScript(Prologue);
                setup.Invoke();
            }
            shell = PowerShell.Create();
            shell.Runspace = bootstrap.Runspace;
            shell.AddScript(request.script);
            results = new PSDataCollection<PSObject>();
            using var noInput = new PSDataCollection<PSObject>(); noInput.Complete();
            shell.EndInvoke(shell.BeginInvoke<PSObject, PSObject>(noInput, results));
        }
        catch (Exception ex) { failure = ex; }
        var items = results?.ToList() ?? [];
        var errorCount = shell?.Streams.Error.Count ?? 0;
        var exitCode = bootstrap.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int code ? code : errorCount > 0 || failure != null ? 1 : 0;
        var streams = CollectStreams(shell);
        shell?.Dispose();
        results?.Dispose();
        var data = new JsonObject
        {
            ["mode"] = mode,
            ["exitCode"] = exitCode,
            ["durationMs"] = stopwatch.ElapsedMilliseconds,
            ["success"] = failure == null && errorCount == 0 && exitCode == 0
        };
        switch (mode)
        {
            case "text": BuildText(data, items); break;
            case "schema": BuildSchema(data, items); break;
            default: BuildRecords(data, items); break;
        }
        if (streams.Count > 0) data["streams"] = streams;
        var frame = new JsonObject { ["id"] = request.id, ["kind"] = "result", ["ok"] = failure == null, ["data"] = data };
        if (failure != null) frame["error"] = Shorten(failure.Message);
        return frame;
    }

    private static void BuildRecords(JsonObject data, List<PSObject> items)
    {
        var records = new JsonArray();
        var size = 0;
        var truncated = false;
        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(CommandInvocation.Project(item, 0), JsonOptions);
            if (records.Count >= MaxRecords || size + json.Length > MaxPayloadCharacters) { truncated = true; break; }
            records.Add(JsonNode.Parse(json));
            size += json.Length;
        }
        data["records"] = records;
        if (truncated) { data["truncated"] = true; data["recordCount"] = items.Count; }
    }

    private static void BuildText(JsonObject data, List<PSObject> items)
    {
        var builder = new StringBuilder();
        var truncated = false;
        foreach (var item in items)
        {
            var text = Shorten(item?.ToString() ?? "");
            if (builder.Length + text.Length > MaxPayloadCharacters) { truncated = true; break; }
            builder.Append(text).Append('\n');
        }
        data["text"] = builder.ToString();
        if (truncated) { data["truncated"] = true; data["itemCount"] = items.Count; }
    }

    private static void BuildSchema(JsonObject data, List<PSObject> items)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new Dictionary<string, PSObject>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item == null) continue;
            var typeName = item.TypeNames.FirstOrDefault() ?? item.BaseObject.GetType().FullName ?? "Object";
            if (!counts.TryGetValue(typeName, out var count)) { order.Add(typeName); samples[typeName] = item; }
            counts[typeName] = count + 1;
        }
        var types = new JsonArray();
        foreach (var typeName in order.Take(MaxSchemaTypes)) types.Add(CommandInvocation.Describe(samples[typeName], counts[typeName]));
        data["types"] = types;
        if (order.Count > MaxSchemaTypes) data["typesTruncated"] = true;
    }

    private static JsonObject CollectStreams(PowerShell? shell)
    {
        var streams = new JsonObject();
        if (shell == null) return streams;
        Add(streams, "errors", shell.Streams.Error.TakeLast(MaxStreamRecords).Select(record => Shorten(record.ToString())));
        Add(streams, "warnings", shell.Streams.Warning.TakeLast(MaxStreamRecords).Select(record => Shorten(record.Message)));
        Add(streams, "verbose", shell.Streams.Verbose.TakeLast(MaxStreamRecords).Select(record => Shorten(record.Message)));
        Add(streams, "debug", shell.Streams.Debug.TakeLast(MaxStreamRecords).Select(record => Shorten(record.Message)));
        Add(streams, "information", shell.Streams.Information.TakeLast(MaxStreamRecords).Select(record => Shorten(record.MessageData?.ToString() ?? "")));
        Add(streams, "progress", shell.Streams.Progress.TakeLast(MaxProgressRecords).Select(record => Shorten($"{record.Activity}: {record.StatusDescription} ({record.PercentComplete}%)")));
        return streams;
    }

    private static void Add(JsonObject streams, string name, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Where(value => value.Length > 0)) array.Add(value);
        if (array.Count > 0) streams[name] = array;
    }

    private static string Shorten(string text) => text.Length <= MaxStringCharacters ? text : text[..MaxStringCharacters];
}
