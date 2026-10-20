using System.Text.Json;
using System.Text;
using HyperlightSandbox.Guest.Python;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Core.CodeAct;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Extensions.AI;

// The native runtime owns thread-local state. Keep initialization, Run, Restore
// and disposal on this dedicated main thread, including between IPC requests.
// stdout is exclusively a line-delimited IPC channel; no model code runs on this host.
Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try
{
    using var init = SandboxProtocol.ReadAsync(WorkerChannel.Input).GetAwaiter().GetResult();
    var root = init.RootElement;
    SandboxProtocol.Validate(root, null);
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
        .Concat([new TransportFunction("_lan_stdout", transport), new TransportFunction("_lan_chunk", transport),
            new TransportFunction("_lan_upload", transport), new TransportFunction("_lan_diagnostic", transport),
            new SleepFunction(), new DispatchFunction(tools, transport)]);
    // No host input mounts and no outbound domains. Microsoft's function manages warm snapshot/restore.
    using var execute = new HyperlightExecuteCodeFunction(options);
    // The SDK initializes the native VM lazily on its first invocation. Do not tell the
    // parent we are ready until the guest and its clean snapshot actually exist: cold
    // startup belongs to SandboxStartupTimeout, never the model's execution budget.
    var warmOutput = execute.InvokeAsync(new AIFunctionArguments { ["code"] = GuestPythonLibraries.Prelude + "import base64\nassert base64.b64decode(base64.b64encode('中文 😀'.encode('utf-16-le'))).decode('utf-16-le') == '中文 😀'\nprint('allagent-sandbox-ready')" }).AsTask().GetAwaiter().GetResult();
    using (var warmResult = JsonDocument.Parse(warmOutput?.ToString() ?? "{}"))
    {
        var warm = warmResult.RootElement;
        if (!warm.GetProperty("success").GetBoolean() || warm.GetProperty("stdout").GetString()?.Trim() != "allagent-sandbox-ready")
            throw new InvalidOperationException("Hyperlight guest initialization failed: " + ToolResult.Clip(warm.GetProperty("stderr").GetString() ?? "No ready marker."));
    }
    // A fixed catalog keeps worker startup free of module probing. It lists only modules
    // verified in both the Hyperlight guest and the Monty fallback, so following the prompt
    // cannot fail on a module this backend lacks.
    const string ModuleGuidance = " Standard-library modules available in this sandbox: base64, binascii, collections, copy, dataclasses, functools, itertools, json, math, os, random, re, sys, time, typing, unicodedata. They are imported automatically on first use and need no import statement; explicit imports remain valid. asyncio, datetime and pathlib are not available in this guest. Any other module fails with ModuleNotFoundError. Do not retry a different module name: host-side automation belongs to call_tool, primarily powershell_script on Windows, while Python is the data-processing and orchestration layer; use call_tool(...) for host filesystem, network and process access.";
    Send(new { kind = "ready", instructions = guidance.BuildInstructions(toolsVisibleToModel: false) + ModuleGuidance, description = guidance.Description });
    while (true)
    {
        using var request = SandboxProtocol.ReadAsync(WorkerChannel.Input).GetAwaiter().GetResult();
        WorkerChannel.ExecutionId = request.RootElement.GetProperty("executionId").GetString() ?? throw new InvalidDataException("Missing execution ID.");
        SandboxProtocol.Validate(request.RootElement, WorkerChannel.ExecutionId);
        if (request.RootElement.GetProperty("kind").GetString() != "execute") throw new InvalidOperationException("Invalid execute request.");
        var code = request.RootElement.GetProperty("code").GetString()!;
        transport.Reset();
        // A trusted prelude bounds native guest stdout before it crosses IPC. Model code
        // runs in a namespace that imports supported standard-library modules on first
        // use, so a missing import cannot fail an otherwise correct program.
        var guarded = GuestPythonLibraries.Prelude + """
            import json as _lan_json
            _lan_host_call_tool = call_tool
            def call_tool(tool_name, /, arguments=None, **parameters):
                if arguments is not None:
                    if not isinstance(arguments, dict):
                        raise TypeError('call_tool second argument must be a dict')
                    if any(key in parameters for key in arguments):
                        raise TypeError('Duplicate tool argument')
                    parameters = {**arguments, **parameters}
                if tool_name.startswith('_lan_'):
                    raise ValueError('Reserved transport function')
                _lan_payload = _lan_json.dumps(parameters)
                for _lan_index in range(0, len(_lan_payload), 1024):
                    _lan_host_call_tool('_lan_upload', text=_lan_payload[_lan_index:_lan_index + 1024])
                _lan_reply = _lan_host_call_tool('_lan_dispatch', name=tool_name)
                if '_lan_parts' in _lan_reply:
                    _lan_fragments = []
                    for _lan_index in range(_lan_reply['_lan_parts']):
                        _lan_fragments.append(_lan_host_call_tool('_lan_chunk', index=_lan_index)['chunk'])
                    return _lan_json.loads(''.join(_lan_fragments))
                return _lan_reply['result']
            _lan_remaining = 20000
            # The guest's native sleep enters Tokio without an active reactor.
            # Wait in a private synchronous callback on the VM's owning thread.
            # The parent still enforces CodeTimeout by terminating this worker.
            import time as _lan_time
            def _lan_sleep(seconds, /):
                if not isinstance(seconds, (int, float)):
                    try:
                        seconds = seconds.__index__()
                    except AttributeError:
                        raise TypeError('sleep requires a real number') from None
                seconds = float(seconds)
                if seconds != seconds:
                    raise ValueError('Invalid value NaN')
                if seconds == float('inf') or seconds == float('-inf'):
                    raise OverflowError('timestamp out of range')
                if seconds < 0:
                    raise ValueError('sleep length must be non-negative')
                while seconds > 0:
                    interval = min(seconds, 60.0)
                    _lan_host_call_tool('_lan_sleep', seconds=interval)
                    seconds -= interval
            _lan_time.sleep = _lan_sleep
            def print(*values, sep=' ', end='\n'):
                global _lan_remaining
                if _lan_remaining <= 0:
                    return
                _lan_text = (sep.join(str(v) for v in values) + end)[:_lan_remaining]
                for _lan_index in range(0, len(_lan_text), 1024):
                    _lan_host_call_tool('_lan_stdout', text=_lan_text[_lan_index:_lan_index + 1024])
                _lan_remaining -= len(_lan_text)
            _lan_builtins = __builtins__ if isinstance(__builtins__, dict) else vars(__builtins__)
            class _lan_namespace(dict):
                def __missing__(self, key):
                    if key.startswith('_') or key in _lan_builtins:
                        raise KeyError(key)
                    try:
                        _lan_imported = __import__(key)
                    except ImportError:
                        raise NameError("name '" + key + "' is not defined; this sandbox has no importable module named '" + key + "'. Use call_tool(...) for host operations, or check the Sandbox runtime module list.") from None
                    self[key] = _lan_imported
                    return _lan_imported
            """ + "\n_lan_source = _lan_json.loads(" + JsonSerializer.Serialize(JsonSerializer.Serialize(code, ToolResult.JsonOptions), ToolResult.JsonOptions) + ")\n" + """
            try:
                exec(compile(_lan_source, 'allagent-code.py', 'exec'), _lan_namespace(dict(globals())))
            except Exception as _lan_error:
                _lan_line = None
                _lan_column = None
                _lan_frames = []
                if isinstance(_lan_error, SyntaxError):
                    _lan_line = _lan_error.lineno
                    _lan_column = _lan_error.offset
                _lan_tb = _lan_error.__traceback__
                while _lan_tb is not None:
                    if _lan_tb.tb_frame.f_code.co_filename == 'allagent-code.py':
                        _lan_line = _lan_tb.tb_lineno
                        _lan_frames.append({'line': _lan_line, 'function': _lan_tb.tb_frame.f_code.co_name})
                    _lan_tb = _lan_tb.tb_next
                _lan_lines = _lan_source.splitlines()
                _lan_hint = None
                if isinstance(_lan_error, ImportError):
                    _lan_hint = "The active sandbox does not provide this module. Use call_tool(...) for host I/O; the Sandbox runtime section lists every verified module. Do not retry a different module name."
                _lan_diagnostic = {'source': 'allagent-code.py', 'line': _lan_line, 'column': _lan_column,
                    'type': type(_lan_error).__name__, 'message': str(_lan_error)[:500], 'frames': _lan_frames[-16:],
                    'sourceLine': _lan_lines[_lan_line - 1][:200] if _lan_line and _lan_line <= len(_lan_lines) else None,
                    'hint': _lan_hint}
                _lan_host_call_tool('_lan_diagnostic', text=_lan_json.dumps(_lan_diagnostic))
                raise
            """;
        var output = execute.InvokeAsync(new AIFunctionArguments { ["code"] = guarded }).AsTask().GetAwaiter().GetResult();
        using var result = JsonDocument.Parse(output?.ToString() ?? "{}");
        var data = result.RootElement;
        var stdout = transport.Stdout;
        var stderr = data.GetProperty("stderr").GetString() ?? "";
        var success = data.GetProperty("success").GetBoolean();
        if (!success && transport.Diagnostic is { } diagnostic && diagnostic.TryGetProperty("line", out var sourceLine) && sourceLine.ValueKind == JsonValueKind.Number)
            stderr = "allagent-code.py:" + sourceLine.GetInt32() + ": " + stderr;
        var hint = !success && transport.Diagnostic is { } failureDiagnostic &&
            failureDiagnostic.TryGetProperty("hint", out var hintElement) && hintElement.ValueKind == JsonValueKind.String
            ? hintElement.GetString() : null;
        var failure = success ? null : "Python execution failed: " + ToolResult.Clip(stderr.Trim(), 1500) + (string.IsNullOrEmpty(hint) ? "" : " " + hint);
        Send(new { kind = "result", result = new ToolResult(success,
            new { stdout = ToolResult.Clip(stdout), stderr = ToolResult.Clip(stderr), exitCode = data.GetProperty("exit_code").GetInt32(), diagnostic = transport.Diagnostic },
            failure, stdout.Length >= ToolResult.MaxOutputCharacters || stderr.Length > ToolResult.MaxOutputCharacters) });
    }
}
catch (Exception error)
{
    Send(new { kind = "error", error = ToolResult.Clip(error.Message) });
    return 1;
}
static void Send(object message)
{
    WorkerChannel.Send(message);
}

sealed class HostFunction(AgentToolDescriptor tool, GuestTransport transport) : AIFunction
{
    public override string Name => tool.Name;
    public override string Description => tool.Description;
    public override JsonElement JsonSchema => tool.Parameters;
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(transport.Wrap(WorkerChannel.Call(Name, JsonSerializer.SerializeToElement(arguments, ToolResult.JsonOptions))));
    }
}

sealed class SleepFunction : AIFunction
{
    public override string Name => "_lan_sleep";
    public override string Description => "Private trusted guest sleep transport.";
    public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { seconds = new { type = "number" } }, required = new[] { "seconds" }
    });
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var seconds = JsonSerializer.SerializeToElement(arguments["seconds"]).GetDouble();
        if (!double.IsFinite(seconds) || seconds is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(arguments));
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
            cancellationToken.ThrowIfCancellationRequested();
        return new(new { slept = true });
    }
}

/// <summary>Bridges the native SDK's fixed 16 KiB shared buffers without reducing the public output budgets.</summary>
sealed class GuestTransport
{
    private const int FragmentCharacters = 1024;
    private readonly StringBuilder _stdout = new();
    private string _result = "";
    private readonly StringBuilder _arguments = new();
    private int _argumentBytes;
    public string Stdout => _stdout.ToString();
    public JsonElement? Diagnostic { get; private set; }
    public void Reset() { _stdout.Clear(); _arguments.Clear(); _argumentBytes = 0; _result = ""; Diagnostic = null; }
    public object ReportDiagnostic(string text)
    {
        if (text.Length > 12000) throw new InvalidDataException("Guest diagnostic exceeds the transport budget.");
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid guest diagnostic.");
        Diagnostic = document.RootElement.Clone();
        return new { received = true };
    }
    public object Upload(string text)
    {
        if (text.Length > FragmentCharacters)
            throw new InvalidDataException("Tool argument fragment exceeds the transport frame size.");
        _argumentBytes = checked(_argumentBytes + Encoding.UTF8.GetByteCount(text));
        if (_argumentBytes > SandboxProtocol.MaxFrameBytes) throw new InvalidDataException("Cumulative tool arguments exceed 16 MiB.");
        _arguments.Append(text);
        return new { uploaded = true };
    }
    public JsonElement TakeArguments()
    {
        var text = _arguments.ToString();
        _arguments.Clear(); _argumentBytes = 0;
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = SandboxProtocol.MaxDepth });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Tool arguments must be an object.");
        return document.RootElement.Clone();
    }
    public object Wrap(JsonElement result)
    {
        _result = result.GetRawText();
        if (Encoding.UTF8.GetByteCount(_result) > SandboxProtocol.MaxFrameBytes - 65536) throw new InvalidDataException("Host tool response exceeds its transport budget.");
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
    public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(name is "_lan_stdout" or "_lan_upload" or "_lan_diagnostic"
        ? (object)new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } }
        : new { type = "object", properties = new { index = new { type = "integer" } }, required = new[] { "index" } });
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == "_lan_upload") return new(transport.Upload(JsonSerializer.SerializeToElement(arguments["text"]).GetString() ?? ""));
        if (name == "_lan_diagnostic") return new(transport.ReportDiagnostic(JsonSerializer.SerializeToElement(arguments["text"]).GetString() ?? ""));
        return new(name == "_lan_stdout"
            ? transport.Write(JsonSerializer.SerializeToElement(arguments["text"]).GetString() ?? "")
            : transport.Receive(JsonSerializer.SerializeToElement(arguments["index"]).GetInt32()));
    }
}

sealed class DispatchFunction(AgentToolDescriptor[] tools, GuestTransport transport) : AIFunction
{
    public override string Name => "_lan_dispatch";
    public override string Description => "Private trusted argument transport.";
    public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" } });
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = JsonSerializer.SerializeToElement(arguments["name"]).GetString();
        var payload = transport.TakeArguments();
        if (!tools.Any(tool => tool.Name == name)) throw new InvalidDataException("Unknown host tool.");
        return new(transport.Wrap(WorkerChannel.Call(name!, payload)));
    }
}

static class WorkerChannel
{
    internal static readonly Stream Input = Console.OpenStandardInput();
    public static string? ExecutionId { get; set; }
    public static void Send(object value, string? callId = null)
    {
        Console.Out.WriteLine(SandboxProtocol.Encode(value, ExecutionId, callId));
        Console.Out.Flush();
    }
    public static JsonElement Call(string name, JsonElement arguments)
    {
        var callId = Guid.NewGuid().ToString("N");
        Send(new { kind = "call", name, arguments }, callId);
        using var response = SandboxProtocol.ReadAsync(Input).GetAwaiter().GetResult();
        SandboxProtocol.Validate(response.RootElement, ExecutionId);
        if (response.RootElement.GetProperty("kind").GetString() != "tool_result" || response.RootElement.GetProperty("callId").GetString() != callId)
            throw new InvalidDataException("Host response call ID mismatch.");
        return response.RootElement.GetProperty("result").Clone();
    }
}
