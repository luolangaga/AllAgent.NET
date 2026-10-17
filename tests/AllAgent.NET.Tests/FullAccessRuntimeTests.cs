using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class FullAccessRuntimeTests
{
    [Fact]
    public void FullAccessIsDefault() => Assert.True(new AgentOptions().FullAccess);

    [Fact]
    public async Task ExplicitHyperlightModeCannotExecuteWithMissingWorker()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true, SandboxMode = SandboxMode.Hyperlight, SandboxWorkerPath = "missing-worker.exe" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(runtime, "import os"));
        Assert.False((await runtime.ProbeSandboxAsync()).Ok);
    }

    [Fact]
    public async Task FullAccessUsesMicroVmAndSkipsHostApproval()
    {
        using var workspace = new TestWorkspace();
        var approvals = 0;
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            FullAccess = true,
            Approver = (_, _) => { approvals++; return ValueTask.FromResult(false); }
        });
        await runtime.CodeAct.PrepareAsync();
        Assert.Contains("Python remains isolated", runtime.CodeAct.Instructions);
        var result = await Execute(runtime, "r = call_tool('file_write', path='actual.txt', content='verified')\nprint(r['ok'])");
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("verified", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "actual.txt")));
        Assert.Equal(0, approvals);
        Assert.False((await Execute(runtime, "open('direct.txt', 'w')")).Ok);
    }

    [Fact]
    public async Task FailedPythonRetainsOutputAndReportsFailure()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true });
        var result = await Execute(runtime, "print('partial result')\nprint(1 / 0)");
        Assert.False(result.Ok);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.NotEqual(0, data.GetProperty("exitCode").GetInt32());
        Assert.Contains("partial result", data.GetProperty("stdout").GetString());
        Assert.Contains("ZeroDivisionError", data.GetProperty("stderr").GetString());
    }

    [Fact]
    public async Task HandledToolFailureIsVisibleWithoutPrintingIt()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true });
        var result = await runtime.CodeAct.ExecuteAsync("execute_code",
            JsonSerializer.SerializeToElement(new { code = "r = call_tool('file_read', path='missing.txt')\nprint('program finished')" }),
            (_, _, _) => new(ToolResult.Failure("missing file")), default);
        Assert.True(result.Ok, result.ToJson());
        var summary = JsonSerializer.SerializeToElement(result.Data).GetProperty("hostCallSummary");
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal("file_read", summary.GetProperty("failureNames")[0].GetString());
    }

    [Fact]
    public async Task ParallelFileReadsWorkThroughActualSandbox()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.txt"), "beta");
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true });
        var result = await Execute(runtime, "r = call_tool('parallel', calls=[{'name':'file_read','arguments':{'path':'a.txt'}}, {'name':'file_read','arguments':{'path':'b.txt'}}])\nassert r['ok'], r['error']\nfor item in r['data']['results']:\n    print(item['data']['content'])");
        Assert.True(result.Ok, result.ToJson());
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Contains("alpha", data.GetProperty("stdout").GetString());
        Assert.Contains("beta", data.GetProperty("stdout").GetString());
        Assert.Equal(2, data.GetProperty("hostCallSummary").GetProperty("total").GetInt32());
    }

    private static Task<ToolResult> Execute(AgentRuntime runtime, string code) => runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, default);
}
