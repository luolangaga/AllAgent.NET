using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ExternalDirectoryTests
{
    [Fact]
    public async Task ConcurrentOnceGrantDoesNotAuthorizeAnotherRequest()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var approvals = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry(async (_, ct) =>
        {
            if (Interlocked.Increment(ref approvals) != 1) return false;
            entered.SetResult(); await release.Task.WaitAsync(ct); return true;
        });
        registry.Register(new SystemTools(new(workspace.Root), new(workspace.Python, workspace.Pwsh, []), new Dictionary<string, string>()));
        var args = JsonSerializer.SerializeToElement(new { path = outside.Root });
        var first = registry.CallAsync("list_directory", args, default).AsTask(); await entered.Task;
        Assert.False((await registry.CallAsync("list_directory", args, default)).Ok);
        release.SetResult(); Assert.True((await first).Ok); Assert.Equal(2, approvals);
    }

    [Fact]
    public async Task ExternalShellDirectoryRequiresSeparateExecutionApproval()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace(); var approvals = new List<string>();
        var registry = new ToolRegistry((request, _) => { approvals.Add(request.Name); return ValueTask.FromResult(request.Name == "external_directory"); });
        registry.Register(workspace.Tools);
        var result = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = "probe", args = Array.Empty<string>(), working_directory = outside.Root }), default);
        Assert.False(result.Ok); Assert.Equal(new[] { "external_directory", "run_shell" }, approvals);
    }

    [Fact]
    public async Task MalformedExternalPathNeverPrompts()
    {
        using var workspace = new TestWorkspace(); var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(true); }); registry.Register(workspace.Tools);
        Assert.False((await registry.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = Path.Combine(workspace.Root, "..", "CON") }), default)).Ok);
        Assert.Equal(0, approvals);
    }

    [Fact]
    public async Task OnceGrantAllowsOriginalOperationButDoesNotLeakIntoNextCall()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(outside.Root, "a.txt"), "external proof"); var asked = 0;
        var registry = new ToolRegistry((request, _) => { Assert.Equal("external_directory", request.Name); asked++; return ValueTask.FromResult(asked == 1); });
        registry.Register(new SystemTools(new(workspace.Root), new(workspace.Python, workspace.Pwsh, []), new Dictionary<string, string>()));
        var args = JsonSerializer.SerializeToElement(new { path = Path.Combine(outside.Root, "a.txt") });
        Assert.True((await registry.CallAsync("file_read", args, default)).Ok);
        Assert.False((await registry.CallAsync("file_read", args, default)).Ok); Assert.Equal(2, asked);
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspacePolicy(workspace.Root).Resolve(outside.Root));
    }

    [Fact]
    public async Task RememberIsScopedToDirectoryAndConversationAndWriteStillRequiresApproval()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var session = Guid.NewGuid(); var permissions = new ExternalDirectoryPermissions(); var directoryRequests = 0; var writes = 0;
        var registry = new ToolRegistry((request, _) =>
        {
            if (request.Name == "external_directory") { directoryRequests++; if (directoryRequests == 1) request.RememberForSession!.Invoke(); return ValueTask.FromResult(directoryRequests == 1); }
            writes++; return ValueTask.FromResult(false);
        });
        registry.Register(new SystemTools(new(workspace.Root, permissions: permissions), new(workspace.Python, workspace.Pwsh, []), new Dictionary<string, string>(), permissions: permissions));
        using (AgentExecutionScope.Enter(false, session))
        {
            Assert.True((await registry.CallAsync("list_directory", JsonSerializer.SerializeToElement(new { path = outside.Root }), default)).Ok);
            Assert.True((await registry.CallAsync("list_directory", JsonSerializer.SerializeToElement(new { path = outside.Root }), default)).Ok);
            var target = Path.Combine(outside.Root, "denied.txt");
            Assert.False((await registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = target, content = "no" }), default)).Ok);
            Assert.False(File.Exists(target)); Assert.Equal(1, directoryRequests); Assert.Equal(1, writes);
            Assert.Single(permissions.List(session));
        }
        using (AgentExecutionScope.Enter(false, Guid.NewGuid()))
            Assert.False((await registry.CallAsync("list_directory", JsonSerializer.SerializeToElement(new { path = outside.Root }), default)).Ok);
        permissions.Clear(session); Assert.Empty(permissions.List(session));
    }

    [Fact]
    public async Task ExternalWritesAreExecutedOnlyAfterBothApprovalsAndAreNotCheckpointedAsWorkspaceFiles()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace(); var approvals = new List<string>();
        var registry = new ToolRegistry((request, _) => { approvals.Add(request.Name); return ValueTask.FromResult(true); });
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(workspace.Root), Path.Combine(workspace.Root, "checkpoints"));
        registry.Register(new SystemTools(new(workspace.Root), new(workspace.Python, workspace.Pwsh, []), new Dictionary<string, string>(), checkpoints));
        var target = Path.Combine(outside.Root, "approved.txt");
        var result = await registry.CallAsync("file_write", JsonSerializer.SerializeToElement(new { path = target, content = "approved" }), default);
        Assert.True(result.Ok, result.ToJson()); Assert.Equal(new[] { "external_directory", "file_write" }, approvals);
        Assert.Equal("approved", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public async Task PermissionBubbleKeepsTranscriptInputAndContextVisible(int width, int height)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = width; console.Profile.Height = height;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new(false, false), console);
        var draft = new InputState(); draft.Set("保留输入草稿"); screen.SetInput(draft, [], "等待授权");
        screen.Append(new Text("上一条执行记录"));
        var request = new ToolApprovalRequest("external_directory", ToolRisk.Read, JsonSerializer.SerializeToElement(new { path = "C:\\Outside\\a.txt", directory = "C:\\Outside", operation = "file_read" })) { RememberForSession = () => { } };
        var preview = ApprovalPreview.Create(request, workspace.Root);
        screen.SetConversationPrompt(new Panel(preview.Render(maximumLines: 2)).Header("需要授权")); console.Write(screen.BuildFrame(width, height));
        Assert.Contains("上一条执行记录", console.Output); Assert.Contains("需要授权", console.Output); Assert.Contains("此会话记住目录", console.Output);
        Assert.Contains("保留输入草稿", console.Output); Assert.Contains("上下文", console.Output);
        screen.SetConversationPrompt(null); Assert.Equal("保留输入草稿", draft.Text);
    }
}
