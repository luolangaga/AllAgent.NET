using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class PowerShellSessionCodeActTests
{
    [Fact]
    public async Task SessionPersistsAcrossExecuteCodeCalls()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { Permission = PermissionMode.Full });
        var first = await Execute(runtime, "r = call_tool('powershell_script', script='$shared = 7')\nprint(r['ok'], r['data']['session'])");
        Assert.True(first.Ok, first.ToJson());
        Assert.Contains("True main", Stdout(first));
        var second = await Execute(runtime, "r = call_tool('powershell_script', script='$shared * 6')\nprint(r['ok'], r['data']['records'][0])");
        Assert.True(second.Ok, second.ToJson());
        Assert.Contains("True 42", Stdout(second));
    }

    private static Task<ToolResult> Execute(AgentRuntime runtime, string code, CancellationToken ct = default) =>
        runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, ct);
    private static string Stdout(ToolResult result) => JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString()!;
}
