using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class FullAccessRuntimeTests
{
    [Fact]
    public void FullAccessIsDefault() => Assert.True(new AgentOptions().FullAccess);

    [Fact]
    public async Task FullAccessStillRejectsImportsAndRequiresSandbox()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { FullAccess = true, SandboxWorkerPath = "missing-worker.exe" });
        Assert.False((await Execute(runtime, "import os")).Ok);
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

    private static Task<ToolResult> Execute(AgentRuntime runtime, string code) => runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, default);
}
