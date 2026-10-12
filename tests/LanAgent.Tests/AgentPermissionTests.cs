using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentPermissionTests
{
    private static JsonElement Args(string path) => JsonSerializer.SerializeToElement(new { path });
    private static AgentRoleDefinition Role(params AgentPermissionRule[] rules) => new("reviewer", "Review", "Review", Permissions: rules);

    [Fact]
    public async Task LastMatchUsesCanonicalPathAliasesAndCannotExpandParentGrant()
    {
        using var w = new TestWorkspace();
        File.WriteAllText(Path.Combine(w.Root, ".env"), "SECRET"); File.WriteAllText(Path.Combine(w.Root, ".env.example"), "SAFE");
        var role = Role(new("*", "*", "allow"), new("read", "*.env*", "deny"), new("file_read", "*.env.example", "allow"));
        var policy = new SubAgentRolePolicy(() => false, w.Root, role: () => role);
        var registry = new ToolRegistry(invocationPolicy: policy.Validate, invocationAuthorization: policy.AuthorizeAsync); registry.Register(w.Tools);
        var blocked = await registry.CallAsync("file_read", Args("sub/../.env"), default);
        Assert.False(blocked.Ok); Assert.Contains("permission rules", blocked.Error);
        Assert.True((await registry.CallAsync("file_read", Args(Path.Combine(w.Root, ".env.example")), default)).Ok);
        var write = await registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = "write.txt", content = "blocked" }), default);
        Assert.False(write.Ok); Assert.False(File.Exists(Path.Combine(w.Root, "write.txt")));
    }

    [Fact]
    public async Task EmptyRulesAskForReadAndFullAccessStillNeedsExplicitHumanApproval()
    {
        using var w = new TestWorkspace(); File.WriteAllText(Path.Combine(w.Root, "safe.txt"), "SAFE"); var approvals = 0;
        var policy = new SubAgentRolePolicy(() => false, w.Root, role: () => Role(), approver: (request, _) =>
        {
            Assert.True(request.RequiresExplicitApproval); Assert.False(request.Preapproved); Assert.Equal(ToolRisk.Read, request.Risk);
            approvals++; return ValueTask.FromResult(approvals == 2);
        });
        await using var runtime = await AgentRuntime.CreateAsync(w.Options with { FullAccess = true, InvocationPolicy = policy.Validate, InvocationAuthorization = policy.AuthorizeAsync });
        Assert.False((await runtime.Tools.CallAsync("file_read", Args("safe.txt"), default)).Ok);
        Assert.True((await runtime.Tools.CallAsync("file_read", Args("safe.txt"), default)).Ok); Assert.Equal(2, approvals);
        var terminalApproval = new ToolsApproval(new TerminalUi(true, false)) { FullAccess = true };
        Assert.False(await terminalApproval.ApproveAsync(new("file_read", ToolRisk.Read, Args("safe.txt")) { RequiresExplicitApproval = true }, default));
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("ask")]
    public async Task RuleChangedDuringApprovalRevokesOldApproval(string effect)
    {
        using var w = new TestWorkspace(); File.WriteAllText(Path.Combine(w.Root, "safe.txt"), "SECRET");
        var role = Role(new AgentPermissionRule("read", "*", "ask")); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new SubAgentRolePolicy(() => false, w.Root, role: () => role, approver: async (_, ct) => { waiting.SetResult(); return await answer.Task.WaitAsync(ct); });
        var registry = new ToolRegistry(invocationPolicy: policy.Validate, invocationAuthorization: policy.AuthorizeAsync); registry.Register(w.Tools);
        var call = registry.CallAsync("file_read", Args("safe.txt"), default).AsTask(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        role = role with { Instructions = "Changed", Permissions = [new("read", "*", effect)] }; answer.SetResult(true);
        var result = await call; Assert.False(result.Ok); Assert.Null(result.Data); Assert.Contains("changed", result.Error);
    }

    [Fact]
    public async Task OptionalShellArgumentsAndQuestionWildcardFollowUpstreamMatching()
    {
        using var w = new TestWorkspace();
        var role = Role(new("*", "*", "deny"), new("bash", "git *", "allow"), new("read", "*/s?fe.txt", "allow"));
        var policy = new SubAgentRolePolicy(() => false, w.Root, role: () => role);
        var shell = w.Tools.Definitions.Single(tool => tool.Name == "run_shell"); var read = w.Tools.Definitions.Single(tool => tool.Name == "file_read");
        Assert.Null(policy.Validate(shell, JsonSerializer.SerializeToElement(new { exe = "git", args = Array.Empty<string>() })));
        Assert.Null(policy.Validate(shell, JsonSerializer.SerializeToElement(new { exe = "git", args = new[] { "status" } })));
        Assert.NotNull(policy.Validate(shell, JsonSerializer.SerializeToElement(new { exe = "gitx", args = Array.Empty<string>() })));
        Assert.Null(policy.Validate(read, Args("safe.txt"))); Assert.NotNull(policy.Validate(read, Args("saffe.txt")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            role = Role();
            var cancelled = new SubAgentRolePolicy(() => false, w.Root, role: () => role, approver: async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return true; });
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await cancelled.AuthorizeAsync(read, Args("safe.txt"), stop.Token);
        });
    }
}
