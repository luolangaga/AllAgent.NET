using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class HyperlightRecoveryTests
{
    [Fact]
    public async Task GuestSleepWaitsAndWorkerRemainsUsableAcrossRestores()
    {
        using var w = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        for (var i = 0; i < 3; i++)
        {
            // Guest virtual time pauses during host callbacks; measure on the host.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var result = await Execute(runtime, """
                import time
                from time import sleep
                sleep(0.1)
                time.sleep(0)
                for value, error in [(-1, ValueError), (float('nan'), ValueError), (float('inf'), OverflowError), ('bad', TypeError)]:
                    try:
                        time.sleep(value)
                    except error:
                        pass
                    else:
                        raise AssertionError('invalid sleep accepted')
                print(call_tool('list_directory', path='.')['ok'])
                """);
            Assert.True(result.Ok, result.ToJson());
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(90));
            Assert.Equal("True\n", JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString());
        }
        Assert.True((await Execute(runtime, "print(42)")).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestSleepHonorsExecutionTimeoutAndCancellation(bool cancel)
    {
        using var w = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with { CodeTimeout = TimeSpan.FromSeconds(1) });
        using var cancellation = new CancellationTokenSource();
        var call = runtime.CodeAct.ExecuteAsync("execute_code",
            JsonSerializer.SerializeToElement(new { code = "import time\ntime.sleep(30)" }),
            runtime.CodeAct.CallToolAsync, cancellation.Token);
        if (cancel)
        {
            cancellation.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            var result = await call.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Ok);
            Assert.Contains("timed out", result.Error);
        }
        Assert.True((await Execute(runtime, "print(42)")).Ok);
    }

    [Fact]
    public async Task BackgroundCurlDownloadSurvivesGuestArgumentTransportAndStatusWait()
    {
        using var w = new TestWorkspace();
        await using var jobs = new AllAgent.NET.Core.Services.BackgroundTasks();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with
        {
            FullAccess = true, BackgroundTasks = jobs,
            ShellAllowlist = new Dictionary<string, string>
            {
                ["curl"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe")
            }
        });
        var content = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
        var source = Path.Combine(w.Root, "source data.bin");
        await File.WriteAllBytesAsync(source, content);
        var args = JsonSerializer.Serialize(new[] { "--fail", "--silent", "--show-error", new Uri(source).AbsoluteUri, "-o", "download file.bin" });
        var result = await Execute(runtime, $"""
            r = call_tool('run_shell', exe='curl', args={args}, background=True)
            assert r['ok'], r
            status = call_tool('shell_status', id=r['data']['taskId'], wait_seconds=10)
            assert status['ok'], status
            print('download checked')
            """);
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal(AllAgent.NET.Core.Services.BackgroundTaskState.Completed, Assert.Single(jobs.List).State);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(w.Root, "download file.bin")));
    }

    private static Task<ToolResult> Execute(AgentRuntime runtime, string code) => runtime.CodeAct.ExecuteAsync("execute_code",
        JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, default);

    [Fact]
    public async Task NativeWorkerSurvivesDelayedRequestsAndHostWaits()
    {
        using var w = new TestWorkspace();
        await using var jobs = new AllAgent.NET.Core.Services.BackgroundTasks();
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with { FullAccess = true, BackgroundTasks = jobs,
            ShellAllowlist = new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe") } });
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(100);
            var result = await Execute(runtime, "r = call_tool('run_shell', exe='probe', args=['quick'], background=True)\nprint(r['ok'])\nprint(call_tool('list_directory', path='.')['ok'])");
            Assert.True(result.Ok, result.ToJson());
            Assert.Equal("True\nTrue\n", JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString());
            Assert.DoesNotContain("reactor", result.ToJson()); Assert.DoesNotContain("poisoned", result.ToJson());
            foreach (var job in jobs.List) await jobs.WaitAsync(job.Id);
        }
        Assert.Equal(8, jobs.List.Count);
    }

    [Fact]
    public async Task FatalNativeResultDiscardsWorkerWithoutReplayingSideEffects()
    {
        using var w = new TestWorkspace(); var workerDirectory = Path.Combine(w.Root, "fault-worker"); Directory.CreateDirectory(workerDirectory);
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "probe"))) File.Copy(source, Path.Combine(workerDirectory, Path.GetFileName(source)));
        var worker = Path.Combine(workerDirectory, "AllAgent.FaultProbe.exe"); File.Copy(Path.Combine(workerDirectory, "AllAgent.NET.ProcessProbe.exe"), worker);
        await using var sandbox = new HyperlightProcessSandbox(w.Options with { SandboxWorkerPath = worker });
        var calls = 0;
        ValueTask<ToolResult> Callback(string name, JsonElement args, CancellationToken ct) { calls++; return ValueTask.FromResult(ToolResult.Success(null)); }
        var first = await sandbox.ExecuteAsync("launch", [], Callback, default);
        Assert.False(first.Ok); Assert.Contains("discarded", first.Error); Assert.Equal(1, calls);
        var second = await sandbox.ExecuteAsync("inspect", [], Callback, default);
        Assert.True(second.Ok, second.ToJson()); Assert.Equal(1, calls);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(workerDirectory, "worker-starts.txt"))).Length);
    }

    [Fact]
    public async Task PowerShellEncodingRunsThroughGuestWithoutMissingModules()
    {
        using var w = new TestWorkspace(); await using var runtime = await AgentRuntime.CreateAsync(w.Options with { FullAccess = true });
        var result = await Execute(runtime, "import base64\nscript = \"Write-Output '中文 😀 quoted \\\"value\\\"'\"\nencoded = base64.b64encode(script.encode('utf-16-le')).decode('ascii')\nr = call_tool('run_shell', exe='pwsh', args=['-NoProfile', '-EncodedCommand', encoded])\nprint(r['ok'])\nprint(r['data']['stdout'])");
        Assert.True(result.Ok, result.ToJson()); var stdout = JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString()!;
        Assert.Contains("True", stdout); Assert.Contains("中文 😀 quoted \"value\"", stdout);
    }

    [Fact]
    public async Task GuestCapabilityErrorsRemainOrdinaryFailuresAndNextCallWorks()
    {
        using var w = new TestWorkspace(); await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var module = await Execute(runtime, "import allagent_missing_module"); Assert.False(module.Ok); Assert.Contains("allagent_missing_module", module.Error);
        var codec = await Execute(runtime, "print('text'.encode('allagent-missing-codec'))"); Assert.False(codec.Ok); Assert.Contains("allagent-missing-codec", codec.Error);
        var next = await Execute(runtime, "print(42)"); Assert.True(next.Ok, next.ToJson());
    }

    [Fact]
    public async Task BundledStdlibSupportsBinaryUnicodeAndCleanSnapshotRestore()
    {
        using var w = new TestWorkspace(); await using var runtime = await AgentRuntime.CreateAsync(w.Options);
        var code = """
            import base64, codecs, struct
            blob = bytes(range(256))
            assert base64.b64decode(base64.b64encode(blob), validate=True) == blob
            assert base64.urlsafe_b64decode(base64.urlsafe_b64encode(blob)) == blob
            assert base64.b85decode(base64.b85encode(blob)) == blob
            assert base64.b64encode(b'') == b''
            try:
                base64.b64decode('!bad!', validate=True)
                raise AssertionError('invalid base64 accepted')
            except base64.binascii.Error:
                pass
            text = '中文 😀\nquotes: "\''
            for encoding in ['utf-16-le', 'UTF_16_LE', 'utf-16-be', 'utf-16']:
                assert text.encode(encoding).decode(encoding) == text
            decoder = codecs.getincrementaldecoder('utf-16-le')()
            encoded = text.encode('utf-16-le')
            assert decoder.decode(encoded[:7]) + decoder.decode(encoded[7:], final=True) == text
            assert struct.unpack('<H', struct.pack('<H', 1234)) == (1234,)
            print('stdlib verified')
            base64.b64encode = None
            """;
        for (var i = 0; i < 3; i++)
        {
            var result = await Execute(runtime, code); Assert.True(result.Ok, result.ToJson());
            Assert.Contains("stdlib verified", JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString());
            Assert.Equal(0, JsonSerializer.SerializeToElement(result.Data).GetProperty("hostCallSummary").GetProperty("total").GetInt32());
        }
        Assert.True((await runtime.ProbeSandboxAsync()).Ok);
        Assert.Empty(Directory.GetFiles(w.Root));
        Assert.DoesNotContain(w.Tools.Definitions, t => t.Name == "powershell_encode");
    }
}
