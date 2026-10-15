using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ExecutionTraceRestoreTests
{
    [Fact]
    public async Task RestoredSessionKeepsActualSourceAndDecodedHostResults()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var code = "r = call_tool('file_read', path='中文.txt')\nprint(r['ok'])";
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "user", Content = "internal-continuation-marker", IsInternal = true });
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "assistant", ReasoningContent = "第一行思考\n完整思考末行", ToolCallsJson = JsonSerializer.Serialize(new[] { new { id = "python", function = new { name = "execute_code", arguments = JsonSerializer.Serialize(new { code }) } } }) });
        var trace = new HostToolTrace("host", "file_read", "{\"path\":\"中文.txt\"}", ToolResult.Success(new { content = "恢复的结果" }).ToJson());
        store.AddMessage(session.Id, new Message { ConversationId = session.Id, Role = "tool", ToolCallId = "python", ToolName = "execute_code", Content = ToolResult.Success(new { stdout = "True" }).ToJson(), HostToolTraceJson = JsonSerializer.Serialize(new[] { trace }, ToolResult.JsonOptions) });
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root); await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 100; console.Profile.Height = 35;
        using var screen = new FullScreenTerminal(store, catalog, mcp, new(false, false), console);
        screen.Session(session.Id, "fixture"); console.Write(screen.BuildFrame(100, 35)); Assert.DoesNotContain("call_tool", console.Output); Assert.Contains("中文.txt", console.Output);
        Assert.DoesNotContain("internal-continuation-marker", console.Output);
        Assert.True(screen.InspectLatest());
        var inspection = new TestConsole(); inspection.Profile.Width = 100; inspection.Profile.Height = 35; inspection.Write(screen.BuildFrame(100, 35));
        Assert.Contains("中文.txt", inspection.Output); Assert.Contains("恢复的结果", inspection.Output); Assert.Contains("成功", inspection.Output);
        Assert.Contains("call_tool", inspection.Output);
        Assert.True(screen.InspectLatestThought());
        var reasoning = new TestConsole(); reasoning.Profile.Width = 100; reasoning.Profile.Height = 35; reasoning.Write(screen.BuildFrame(100, 35));
        Assert.Contains("第一行思考", reasoning.Output); Assert.Contains("完整思考末行", reasoning.Output);
    }
}
