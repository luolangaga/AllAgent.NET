using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class CodeActTests
{
    [Fact]
    public async Task DefensiveExceptionHandlingAndStringBatchOperationsRunInGuest()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "try:\n    value = int('invalid')\nexcept ValueError as error:\n    print('caught')\nfinally:\n    print('done')\nfor name in ['one', 'two']:\n    print(name.replace('o', 'O'))");
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("caught", Stdout(result)); Assert.Contains("done", Stdout(result)); Assert.Contains("One", Stdout(result));
    }

    [Theory]
    [InlineData("try:\n    pass\nexcept Exception as call_tool:\n    pass")]
    [InlineData("try:\n    pass\nexcept Exception as _lan_reply:\n    pass")]
    [InlineData("try:\n    pass\nexcept print('bad'):\n    pass")]
    public async Task ExceptionHandlersCannotShadowReservedNamesOrEvaluateTypes(string code)
    {
        using var workspace = new TestWorkspace();
        Assert.NotNull(await new PythonCodeValidator(workspace.Python).ValidateAsync(code));
    }
    [Fact]
    public async Task LargeToolArgumentsCrossGuestBufferWithoutSplittingFiles()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { Permission = PermissionMode.Full });
        var content = new string('中', 9000) + new string('x', 18000);
        var code = "r = call_tool('file_write', path='large.cs', content=" + JsonSerializer.Serialize(content, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + ")\nprint(r['ok'])";
        var result = await Execute(runtime, code);
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("True", Stdout(result));
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(workspace.Root, "large.cs")));
    }
    [Fact]
    public async Task HyperlightCallbackPreservesExternalDirectoryConversationGrant()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var session = Guid.NewGuid(); var permissions = new ExternalDirectoryPermissions(); var approvals = 0;
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            ExternalDirectoryPermissions = permissions,
            Approver = (request, _) => { approvals++; request.RememberForSession!.Invoke(); return ValueTask.FromResult(true); }
        });
        using (AgentExecutionScope.Enter(false, session))
        {
            var code = "r = call_tool('list_directory', path=" + JsonSerializer.Serialize(outside.Root) + ")\nprint(r['ok'])";
            var first = await Execute(runtime, code); Assert.True(first.Ok, first.ToJson()); Assert.Contains("True", Stdout(first));
            var second = await Execute(runtime, code); Assert.True(second.Ok, second.ToJson()); Assert.Contains("True", Stdout(second));
            Assert.Equal(1, approvals); Assert.Single(permissions.List(session)); Assert.Empty(permissions.List(Guid.Empty));
        }
    }
    [Fact]
    public async Task FailedHostResultRemainsInspectableInsidePython()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        await runtime.CodeAct.PrepareAsync();
        Assert.Contains("cannot be invoked directly", runtime.CodeAct.Instructions);
        Assert.Contains("list_directory", runtime.CodeAct.Instructions);
        var result = await Execute(runtime, "r = call_tool('file_read', path='missing.txt')\nif not r['ok']:\n    print(r['error'])\nr = call_tool('list_directory', path='.')\nprint(r['ok'])");
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("True", Stdout(result));
        var denied = await Execute(runtime, "r = call_tool('file_write', path='denied.txt', content='must not be written')\nif not r['ok']:\n    print(r['error'])\nprint(42)");
        Assert.True(denied.Ok, denied.ToJson()); Assert.Contains("approval denied", Stdout(denied)); Assert.Contains("42", Stdout(denied));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "denied.txt")));
        Assert.False((await Execute(runtime, "print(r)")).Ok);
    }
    [Fact]
    public async Task WaitingForHostQuestionDoesNotConsumeGuestBudget()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { CodeTimeout = TimeSpan.FromSeconds(8) });
        var probe = await runtime.ProbeSandboxAsync(); Assert.True(probe.Ok, probe.ToJson());
        var result = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "r = call_tool('ask_user', question='Continue?', choices=[])\nprint(r['data']['answer'])" }), async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(9), ct); return ToolResult.Success(new { answer = "human reply" });
        }, default);
        Assert.True(result.Ok, result.Error); Assert.Contains("human reply", result.ToJson());
        var timeout = await Execute(runtime, "while True:\n    pass"); Assert.False(timeout.Ok); Assert.Contains("timed out", timeout.Error);
    }
    [Theory]
    [InlineData("import os")]
    [InlineData("from pathlib import Path")]
    [InlineData("eval('1+1')")]
    [InlineData("exec('print(1)')")]
    [InlineData("open('x')")]
    [InlineData("print((1).__class__)")]
    [InlineData("getattr({}, 'get')")]
    [InlineData("http_get('https://example.com')")]
    [InlineData("call_tool = print")]
    [InlineData("print = 5")]
    [InlineData("f = call_tool; f('file_read', path='x')")]
    [InlineData("def f():\n    import os")]
    [InlineData("def f():\n    return open('x')\nf()")]
    [InlineData("def call_tool(): pass")]
    [InlineData("def f(print): pass")]
    [InlineData("def f(_secret): pass")]
    [InlineData("@print\ndef f(): pass")]
    [InlineData("def f(): pass\nf = call_tool\nf(str('file_read'), path='x')")]
    [InlineData("def f(): pass\ndef g(f):\n    f(str('file_read'), path='x')\ng(call_tool)")]
    [InlineData("call_tool('file_read', **{'path':'x'})")]
    [InlineData("call_tool(str('file_read'), path='x')")]
    [InlineData("print('x'.format())")]
    public async Task ValidatorRejectsForbiddenCode(string code)
    {
        using var workspace = new TestWorkspace();
        Assert.NotNull(await new PythonCodeValidator(workspace.Python).ValidateAsync(code));
    }

    [Fact]
    public async Task ActualGuestSupportsBasicTypeChecksWithoutTruthinessAssumptions()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "for value in [None, {}, [], 'text', 42, {'taskId': 'fixture'}]:\n    if isinstance(value, dict):\n        print(value.get('taskId', 'object'))\n    else:\n        print('other')\nprint(isinstance([], (dict, list)))");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("other\nobject\nother\nother\nother\nfixture\nTrue\n", Stdout(result));
    }

    [Theory]
    [InlineData("value = 1\nprint(value / 0)", 2, "ZeroDivisionError")]
    [InlineData("value = 1\nif value\n    pass", 2, "SyntaxError")]
    [InlineData("def fail():\n    return 1 / 0\nfail()", 2, "ZeroDivisionError")]
    public async Task GuestRuntimeTracebackReportsTheUserSourceLine(string code, int line, string type)
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, code);
        Assert.False(result.Ok);
        Assert.True(result.ToJson().Contains("allagent-code.py"), result.ToJson());
        var diagnostic = JsonSerializer.SerializeToElement(result.Data).GetProperty("diagnostic");
        Assert.Equal(line, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(type, diagnostic.GetProperty("type").GetString());
        Assert.Contains("allagent-code.py:" + line, result.Error);
        Assert.DoesNotContain("Code rejected", result.ToJson());
    }

    [Fact]
    public async Task GuestDiagnosticWrapperPreservesUnicodeAndLiteralEscapes()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "print('emoji 😀 中文')\nprint(r'\\u1234\\n')");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("emoji 😀 中文\n\\u1234\\n\n", Stdout(result));
    }

    [Fact]
    public async Task SourceIsPassedToSandboxAndRuntimeErrorIsReturnedWithoutWhitelistGuidance()
    {
        var sandbox = new RuntimeErrorSandbox();
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("missing-validator-python"), sandbox);
        const string code = "reader = call_tool\nreader('file_read', **{'path':'x'})";
        var result = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), (_, _, _) => throw new InvalidOperationException(), default);
        Assert.Equal(code, sandbox.Source);
        Assert.False(result.Ok);
        Assert.Equal("fixture runtime error", result.Error);
        Assert.DoesNotContain("source validation", provider.Instructions);
        Assert.DoesNotContain("Source policy", provider.Instructions);
        Assert.DoesNotContain("unpacking", Assert.Single(provider.ModelTools).Description);
    }

    [Fact]
    public async Task InstructionsRejectPythonPackagesWithoutInjectingLegacyCatalogOrUserCommands()
    {
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new RuntimeErrorSandbox(),
            pythonLibraryContext: () => throw new InvalidOperationException("Legacy pip catalog must not be read"));
        provider.PowerShellCapabilitiesProvider = () => "[]";
        var instructions = provider.Instructions;
        Assert.Contains("Do not install or import third-party Python packages", instructions);
        Assert.DoesNotContain("/powershell", instructions);
        Assert.DoesNotContain("pip.md", instructions);
        Assert.DoesNotContain("Installed host libraries", instructions);
    }

    private sealed class RuntimeErrorSandbox : ICodeSandbox
    {
        public string? Source { get; private set; }
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        { Source = code; return Task.FromResult(ToolResult.Failure("fixture runtime error")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ActualGuestExecutesDynamicCallsAndReportsRuntimeErrors()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sample.txt"), "runtime boundary");
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "reader = call_tool\nargs = {'path':'sample.txt'}\nr = reader(str('file_read'), **args)\nprint(r['data']['content'])");
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("runtime boundary", Stdout(result));
        var failed = await Execute(runtime, "unknown_function()");
        Assert.False(failed.Ok);
        Assert.DoesNotContain("Code rejected", failed.ToJson());
        Assert.Contains("unknown_function", failed.ToJson());
        Assert.True((await Execute(runtime, "print(42)")).Ok);
    }

    [Fact]
    public async Task ActualGuestSupportsImportsComprehensionsTupleUnpackingAndLambda()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "import json\nvalues = [('b', 2), ('a', 1)]\nprint(json.dumps([name.strip() for name, number in sorted(values, key=lambda pair: pair[1])]))");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("[\"a\", \"b\"]\n", Stdout(result));
    }

    [Fact]
    public async Task ActualGuestImportsSupportedModulesOnFirstUseWithoutImportStatements()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "values = sorted([3, 1, 2])\ndef encode():\n    return base64.b64encode(bytes(str(math.floor(2.7)), 'ascii')).decode('ascii')\nprint(json.dumps({'values': values, 'encoded': encode(), 'scrubbed': re.sub('[0-9]+', '#', 'a12b')}))");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("{\"values\": [1, 2, 3], \"encoded\": \"Mg==\", \"scrubbed\": \"a#b\"}\n", Stdout(result));
        var explicitImport = await Execute(runtime, "import json\nfrom math import floor\nprint(json.dumps(floor(1.9)))");
        Assert.True(explicitImport.Ok, explicitImport.ToJson());
        Assert.Equal("1\n", Stdout(explicitImport));
        var unavailable = await Execute(runtime, "print(hashlib.md5(b'x').hexdigest())");
        Assert.False(unavailable.Ok);
        Assert.Contains("hashlib", unavailable.ToJson());
    }

    [Fact]
    public async Task SharedModuleCatalogImportsInActiveSandbox()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var modules = new[]
        {
            "base64", "binascii", "collections", "copy", "dataclasses", "functools", "itertools",
            "json", "math", "os", "random", "re", "sys", "time", "typing", "unicodedata"
        };
        var code = "import json as _probe_json\n_failed = []\n" + string.Join("\n", modules.Select(name =>
            "try:\n    import " + name + "\nexcept Exception:\n    _failed.append('" + name + "')")) +
            "\nprint(_probe_json.dumps(_failed))";
        var result = await Execute(runtime, code);
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("[]\n", Stdout(result));
    }

    [Fact]
    public async Task SandboxRuntimeListsVerifiedModulesAndMissingImportsAreActionable()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        await runtime.CodeAct.PrepareAsync();
        var instructions = runtime.CodeAct.Instructions;
        var diagnostics = JsonSerializer.SerializeToElement(runtime.CodeAct.GetPromptDiagnostics());
        var backend = diagnostics.TryGetProperty("sandbox", out var sandbox) && sandbox.ValueKind == JsonValueKind.Object
            ? sandbox.TryGetProperty("backend", out var backendLower) ? backendLower.GetString()
              : sandbox.TryGetProperty("Backend", out var backendUpper) ? backendUpper.GetString() : null
            : null;
        Assert.True(backend is "hyperlight" or "monty", diagnostics.ToString());
        Assert.Contains("json", instructions);
        Assert.Contains("call_tool", instructions);
        Assert.Contains("PowerShell is the primary host automation surface", instructions);
        if (backend == "hyperlight")
            Assert.Contains("Standard-library modules available in this sandbox", instructions);
        else
            Assert.Contains("Shared standard-library modules for both sandboxes", instructions);

        var unavailable = await Execute(runtime, "import numpy\nprint('must not run')");
        Assert.False(unavailable.Ok);
        Assert.Contains("numpy", unavailable.ToJson());
        Assert.Contains("call_tool", unavailable.ToJson());

        var typo = await Execute(runtime, "print(undefined_value)");
        Assert.False(typo.Ok);
        Assert.Contains("undefined_value", typo.ToJson());
    }

    [Fact]
    public async Task ValidatorAcceptsPureLogicAndCallTool()
    {
        using var workspace = new TestWorkspace();
        var code = "r = call_tool('list_directory', path='.')\nif r['ok']:\n    names = [x['name'] for x in r['data']['entries']]\n    print(sorted(names))";
        Assert.Null(await new PythonCodeValidator(workspace.Python).ValidateAsync(code));
    }

    [Theory]
    [InlineData("def f(): pass\nf()")]
    [InlineData("def sh(args, t=60, wd='.'):\n    r = call_tool('run_shell', exe='rg', args=args, working_directory=wd, timeout_seconds=t)\n    d = r.get('data') or {}\n    return str(d.get('stdout') or '')[:9000]\nprint(sh(['--files']))")]
    public async Task ValidatorAcceptsCustomFunctions(string code)
    {
        using var workspace = new TestWorkspace();
        Assert.Null(await new PythonCodeValidator(workspace.Python).ValidateAsync(code));
    }

    [Fact]
    public async Task ActualHyperlightExecutesHelperFunctionWithHostCall()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sample.txt"), "helper result");
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "def read(path='sample.txt'):\n    r = call_tool('file_read', path=path)\n    return r['data']['content'] if r['ok'] else r['error']\nprint(read())");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("helper result\n", Stdout(result));
        Assert.False((await Execute(runtime, "print(read())")).Ok);
    }

    [Fact]
    public async Task ActualHyperlightCallsCSharpAndRestoresSnapshot()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sample.txt"), "hello microVM");
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        Assert.Equal("execute_code", Assert.Single(runtime.CodeAct.ModelTools).Name);
        var result = await Execute(runtime, "r = call_tool('file_read', path='sample.txt')\nx = 123\nprint(r['data']['content'])");
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("hello microVM", Stdout(result));
        var second = await Execute(runtime, "print(x)");
        Assert.False(second.Ok); // No variables survive the previous microVM invocation.
        Assert.True((await Execute(runtime, "print(7*6)")).Ok);
    }

    [Fact]
    public async Task ActualGuestOutputIsClipped()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var result = await Execute(runtime, "for i in range(100):\n    print('x' * 200)");
        Assert.True(result.Ok, result.ToJson()); Assert.True(result.Truncated); Assert.Equal(ToolResult.MaxOutputCharacters, Stdout(result).Length);
    }

    [Fact]
    public async Task ActualGuestTransportsUnicodeOutputAndLargeHostResultsWithoutExposingPrivateTools()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        runtime.Tools.Register(new ExtraTool());
        await runtime.CodeAct.PrepareAsync();
        var result = await Execute(runtime, "r = call_tool('extra', value='large-host-fixture')\nprint(len(r['data']['value']))\nprint(r['data']['value'])\nprint('中' * 20000)");
        Assert.True(result.Ok, result.ToJson()); Assert.True(result.Truncated);
        Assert.Equal(ToolResult.MaxOutputCharacters, Stdout(result).Length);
        Assert.StartsWith("9600\n网页内容", Stdout(result));
        Assert.EndsWith("中", Stdout(result));
        Assert.Contains("extra", runtime.CodeAct.Instructions);
        Assert.DoesNotContain(runtime.Tools.Definitions, tool => tool.Name.StartsWith("_lan_"));
        Assert.DoesNotContain("_lan_stdout", runtime.CodeAct.Instructions);
        Assert.DoesNotContain("_lan_chunk", runtime.CodeAct.Instructions);
        Assert.DoesNotContain("_lan_stdout", Assert.Single(runtime.CodeAct.ModelTools).Description);
        Assert.DoesNotContain("_lan_chunk", Assert.Single(runtime.CodeAct.ModelTools).Description);
        var denied = await Execute(runtime, "print(call_tool('_lan_chunk', index=0))");
        Assert.False(denied.Ok); Assert.Contains("Reserved transport", denied.ToJson());
        var next = await Execute(runtime, "print('fresh output')");
        Assert.True(next.Ok, next.ToJson()); Assert.Equal("fresh output\n", Stdout(next));
    }

    [Fact]
    public async Task ActualGuestCanConsumeAndPrintTheFullWebFetchContentBudget()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        using var client = new HttpClient(new WebPageHandler());
        runtime.Tools.Replace<WebTools>(new WebTools(null, client, client));
        var result = await Execute(runtime, "page = call_tool('web_fetch', url='https://8.8.8.8/page', max_characters=20000)\nprint(len(page['data']['content']))\nprint(page['data']['content'])");
        Assert.True(result.Ok, result.ToJson()); Assert.True(result.Truncated);
        Assert.Equal(ToolResult.MaxOutputCharacters, Stdout(result).Length);
        Assert.StartsWith("20000\n", Stdout(result));
        Assert.EndsWith("x", Stdout(result));
    }

    private sealed class WebPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(new string('x', 21000), System.Text.Encoding.UTF8, "text/plain") });
        }
    }

    [Fact]
    public async Task WorkerRejectsPluginToolNamesThatCollideWithPrivateTransport()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        runtime.Tools.Register(new ReservedTool());
        var result = await Execute(runtime, "print('must not run')");
        Assert.False(result.Ok); Assert.Contains("reserved for trusted sandbox transport", result.Error);
    }

    private sealed class ReservedTool : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = [new("_lan_chunk", "Reserved collision fixture",
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => throw new InvalidOperationException("Must not run.");
    }

    [Fact]
    public async Task RegisteredToolChangesRebuildTheActualSandbox()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        Assert.True((await runtime.ProbeSandboxAsync()).Ok);
        runtime.Tools.Register(new ExtraTool());
        await runtime.CodeAct.PrepareAsync();
        Assert.Contains("extra", runtime.CodeAct.Instructions);
        var result = await Execute(runtime, "r = call_tool('extra', value='new tool')\nprint(r['data']['value'])");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("new tool\n", Stdout(result));
    }

    private sealed class ExtraTool : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = [new("extra", "Test extra host tool",
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false,"properties":{"value":{"type":"string"}},"required":["value"]}"""), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            var value = arguments.GetProperty("value").GetString();
            return new(ToolResult.Success(new { value = value == "large-host-fixture" ? string.Concat(Enumerable.Repeat("网页内容", 2400)) : value }));
        }
    }

    [Fact]
    public async Task ActualWorkerIsKilledOnCancellationAndCanRestart()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options);
        var probe = await runtime.ProbeSandboxAsync();
        Assert.True(probe.Ok, probe.ToJson());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(runtime, "while True:\n    pass", cancel.Token));
        var restarted = await Execute(runtime, "print(42)");
        Assert.True(restarted.Ok, restarted.ToJson());
    }

    [Fact]
    public async Task MissingExplicitHyperlightWorkerFailsClosed()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { SandboxMode = SandboxMode.Hyperlight, SandboxWorkerPath = Path.Combine(workspace.Root, "missing.exe") });
        Assert.False((await runtime.ProbeSandboxAsync()).Ok);
        var rejected = await runtime.CodeAct.ExecuteAsync("file_read", JsonSerializer.SerializeToElement(new { path = "x" }), runtime.CodeAct.CallToolAsync, default);
        Assert.False(rejected.Ok);
    }

    private static Task<ToolResult> Execute(AgentRuntime runtime, string code, CancellationToken ct = default) =>
        runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, ct);
    private static string Stdout(ToolResult result) => JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString()!;
}
