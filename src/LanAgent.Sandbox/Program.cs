using System.Text.Json;
using System.Text;
using HyperlightSandbox.Guest.Python;
using LanAgent.Core.Tools;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Extensions.AI;

// stdout is exclusively a line-delimited IPC channel; no model code runs on this host.
Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try
{
    using var init = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? throw new IOException("Missing init."));
    var root = init.RootElement;
    if (root.GetProperty("kind").GetString() != "init") throw new InvalidOperationException("Invalid init.");
    var guest = root.TryGetProperty("guestPath", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : PythonGuestModule.GetModulePath();
    var tools = root.GetProperty("tools").Deserialize<AgentToolDescriptor[]>(ToolResult.JsonOptions) ?? [];
    if (tools.Any(tool => tool.Name.StartsWith("_lan_", StringComparison.Ordinal)))
        throw new InvalidOperationException("Host tool names beginning with '_lan_' are reserved for trusted sandbox transport.");
    var options = HyperlightCodeActProviderOptions.CreateForWasm(guest);
    options.HeapSize = "128Mi";
    options.StackSize = "128Mi";
    var transport = new GuestTransport();
    var visibleTools = tools.Select(tool => (AIFunction)new HostFunction(tool, transport)).ToArray();
    options.Tools = visibleTools;
    // This function is never invoked: the SDK snapshots its public-only catalog for model guidance.
    // The actual function adds private transport callbacks that must never appear in that guidance.
    using var guidance = new HyperlightExecuteCodeFunction(options);
    options.Tools = visibleTools
        .Concat([new TransportFunction("_lan_stdout", transport), new TransportFunction("_lan_chunk", transport)]);
    // No host input mounts and no outbound domains. Microsoft's function manages warm snapshot/restore.
    using var execute = new HyperlightExecuteCodeFunction(options);
    // The SDK initializes the native VM lazily on its first invocation. Do not tell the
    // parent we are ready until the guest and its clean snapshot actually exist: cold
    // startup belongs to SandboxStartupTimeout, never the model's execution budget.
    var warmOutput = await execute.InvokeAsync(new AIFunctionArguments { ["code"] = "print('lanagent-sandbox-ready')" });
    using (var warmResult = JsonDocument.Parse(warmOutput?.ToString() ?? "{}"))
    {
        var warm = warmResult.RootElement;
        if (!warm.GetProperty("success").GetBoolean() || warm.GetProperty("stdout").GetString()?.Trim() != "lanagent-sandbox-ready")
            throw new InvalidOperationException("Hyperlight guest initialization failed: " + ToolResult.Clip(warm.GetProperty("stderr").GetString() ?? "No ready marker."));
    }
    Send(new { kind = "ready", instructions = guidance.BuildInstructions(toolsVisibleToModel: false), description = guidance.Description });
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        using var request = JsonDocument.Parse(line);
        if (request.RootElement.GetProperty("kind").GetString() != "execute") throw new InvalidOperationException("Invalid execute request.");
        var code = request.RootElement.GetProperty("code").GetString()!;
        transport.Reset();
        // A trusted prelude bounds native guest stdout before it crosses IPC. Names are inaccessible to model code.
        var guarded = """
            import json as _lan_json
            _lan_host_call_tool = call_tool
            def call_tool(tool_name, **parameters):
                if tool_name.startswith('_lan_'):
                    raise ValueError('Reserved transport function')
                _lan_reply = _lan_host_call_tool(tool_name, **parameters)
                if '_lan_parts' in _lan_reply:
                    _lan_fragments = []
                    for _lan_index in range(_lan_reply['_lan_parts']):
                        _lan_fragments.append(_lan_host_call_tool('_lan_chunk', index=_lan_index)['chunk'])
                    return _lan_json.loads(''.join(_lan_fragments))
                return _lan_reply['result']
            _lan_remaining = 20000
            def print(*values, sep=' ', end='\n'):
                global _lan_remaining
                if _lan_remaining <= 0:
                    return
                _lan_text = (sep.join(str(v) for v in values) + end)[:_lan_remaining]
                for _lan_index in range(0, len(_lan_text), 1024):
                    _lan_host_call_tool('_lan_stdout', text=_lan_text[_lan_index:_lan_index + 1024])
                _lan_remaining -= len(_lan_text)
            """ + "\n" + code;
        var output = await execute.InvokeAsync(new AIFunctionArguments { ["code"] = guarded });
        using var result = JsonDocument.Parse(output?.ToString() ?? "{}");
        var data = result.RootElement;
        var stdout = transport.Stdout;
        var stderr = data.GetProperty("stderr").GetString() ?? "";
        var success = data.GetProperty("success").GetBoolean();
        Send(new { kind = "result", result = new ToolResult(success,
            new { stdout = ToolResult.Clip(stdout), stderr = ToolResult.Clip(stderr), exitCode = data.GetProperty("exit_code").GetInt32() },
            success ? null : "Python execution failed: " + ToolResult.Clip(stderr.Trim(), 1500), stdout.Length >= ToolResult.MaxOutputCharacters || stderr.Length > ToolResult.MaxOutputCharacters) });
    }
}
catch (Exception error)
{
    Send(new { kind = "error", error = ToolResult.Clip(error.Message) });
    return 1;
}
return 0;

static void Send(object message)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(message, ToolResult.JsonOptions));
    Console.Out.Flush();
}

sealed class HostFunction(AgentToolDescriptor tool, GuestTransport transport) : AIFunction
{
    public override string Name => tool.Name;
    public override string Description => tool.Description;
    public override JsonElement JsonSchema => tool.Parameters;
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Out.WriteLine(JsonSerializer.Serialize(new { kind = "call", name = Name, arguments }, ToolResult.JsonOptions));
        Console.Out.Flush();
        using var response = JsonDocument.Parse(Console.In.ReadLine() ?? throw new IOException("Host channel closed."));
        if (response.RootElement.GetProperty("kind").GetString() != "tool_result") throw new IOException("Invalid host response.");
        // The native Python bridge treats a top-level 'error' as a RuntimeError.
        // Preserve the LanAgent ok/data/error contract as a value, so model code can
        // handle denied/invalid operations with ordinary if statements. Only trusted
        // prelude code unwraps this internal transport envelope.
        return new(transport.Wrap(response.RootElement.GetProperty("result")));
    }
}

/// <summary>Bridges the native SDK's fixed 16 KiB shared buffers without reducing the public output budgets.</summary>
sealed class GuestTransport
{
    private const int FragmentCharacters = 1024;
    private readonly StringBuilder _stdout = new();
    private string _result = "";
    public string Stdout => _stdout.ToString();
    public void Reset() { _stdout.Clear(); _result = ""; }
    public object Wrap(JsonElement result)
    {
        _result = result.GetRawText();
        if (_result.Length > 64000) throw new InvalidDataException("Host tool response exceeds its transport budget.");
        if (Encoding.UTF8.GetByteCount(_result) <= 6000) return new { result = result.Clone() };
        return new { _lan_parts = (_result.Length + FragmentCharacters - 1) / FragmentCharacters };
    }
    public object Receive(int index)
    {
        if (index < 0 || index >= (_result.Length + FragmentCharacters - 1) / FragmentCharacters)
            throw new ArgumentOutOfRangeException(nameof(index));
        var offset = index * FragmentCharacters;
        return new { chunk = _result.Substring(offset, Math.Min(FragmentCharacters, _result.Length - offset)) };
    }
    public object Write(string text)
    {
        if (text.Length > FragmentCharacters * 2) throw new InvalidDataException("Guest output fragment is too large.");
        _stdout.Append(text.AsSpan(0, Math.Min(text.Length, ToolResult.MaxOutputCharacters - _stdout.Length)));
        return new { written = true };
    }
}

sealed class TransportFunction(string name, GuestTransport transport) : AIFunction
{
    public override string Name => name;
    public override string Description => "Private trusted transport; never invoke from model code.";
    public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(name == "_lan_stdout"
        ? (object)new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } }
        : new { type = "object", properties = new { index = new { type = "integer" } }, required = new[] { "index" } });
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(name == "_lan_stdout"
            ? transport.Write(JsonSerializer.SerializeToElement(arguments["text"]).GetString() ?? "")
            : transport.Receive(JsonSerializer.SerializeToElement(arguments["index"]).GetInt32()));
    }
}
