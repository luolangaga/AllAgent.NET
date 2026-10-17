using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using AllAgent.NET.Core.Data;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Terminal interaction")]
public sealed class TerminalExecutionCacheTests
{
    [Fact]
    public async Task StandaloneHostCallsUseCurrentToolBlocksInsteadOfLegacyBulletOutput()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var skills = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var ui = new TerminalUi(false, false);
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, skills, mcp, ui, console);
        screen.Enter();
        await ui.RenderAsync(new ToolCallStarted("orphan", "file_read", "{\"path\":\"proof.txt\"}"), default);
        await ui.RenderAsync(new ToolCallCompleted("orphan", "file_read", ToolResult.Success(new { content = "standalone-result" }).ToJson()), default);
        var text = string.Concat(screen.RenderFrame(100, 35).Select(segment => segment.Text));
        Assert.Contains("READ", text);
        Assert.Contains("proof.txt", text);
        Assert.Contains("standalone-result", text);
        Assert.DoesNotContain("● 读取", text);
        Assert.DoesNotContain("代码 ›", text);
    }

    [Fact]
    public async Task PruningKeepsInFlightHostResultsAndActiveParents()
    {
        var ui = new TerminalUi(false, false);
        var active = new CodeExecutionBlock("active", "call_tool('file_write')");
        ui.TrackCodeBlock(active);
        await ui.RenderAsync(new ToolCallStarted("host", "file_write", "{}") { ParentId = active.Id }, default);
        FillCompleted(ui);
        ui.TrackCodeBlock(new CodeExecutionBlock("next", "print(1)"));
        await ui.RenderAsync(new ToolCallCompleted("host", "file_write", ToolResult.Success(new { characters = 12 }).ToJson()), default);
        Assert.True(active.Invocations.Single().Ok);
        await ui.RenderAsync(new ToolCallStarted("host2", "file_write", "{}") { ParentId = active.Id }, default);
        Assert.Equal(2, active.Invocations.Count);
    }

    [Fact]
    public async Task PruningKeepsNewExecutionWhenProviderReusesOldestId()
    {
        var ui = new TerminalUi(false, false);
        ui.TrackCodeBlock(new CodeExecutionBlock("extra", "print(1)"));
        for (var i = 0; i < 300; i++)
            ui.TrackCodeBlock(new CodeExecutionBlock("active-" + i, "print(1)"));
        var reused = new CodeExecutionBlock("extra", "call_tool('file_write')");
        ui.TrackCodeBlock(reused);
        await ui.RenderAsync(new ToolCallStarted("host", "file_write", "{}") { ParentId = reused.Id }, default);
        Assert.Single(reused.Invocations);
    }

    private static void FillCompleted(TerminalUi ui)
    {
        for (var i = 0; i < 300; i++)
        {
            var block = new CodeExecutionBlock("completed-" + i, "print(1)");
            block.Complete(new ToolCallCompleted(block.Id, "execute_code", ToolResult.Success(new { }).ToJson()));
            ui.TrackCodeBlock(block);
        }
    }
}
