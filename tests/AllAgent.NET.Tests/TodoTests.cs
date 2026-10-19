using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class TodoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AllAgent.TodoTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ChecklistSurvivesReopenAndReadsDoNotWrite()
    {
        var session = new Conversation();
        using (var store = new DataStore(_root))
        {
            store.AddConversation(session);
            store.SetTodos(session.Id, [new("1", "验证实现", "in_progress")]);
            var writes = store.PersistenceWrites;
            Assert.Single(store.GetTodos(session.Id)); Assert.Single(store.GetTodos(session.Id));
            Assert.Equal(writes, store.PersistenceWrites);
        }
        using var reopened = new DataStore(_root);
        Assert.Equal(new TodoItem("1", "验证实现", "in_progress"), Assert.Single(reopened.GetTodos(session.Id)));
        reopened.SetTodos(session.Id, []);
        Assert.Empty(reopened.GetTodos(session.Id));
    }

    [Theory]
    [InlineData("[{\"id\":\"1\",\"content\":\"a\",\"status\":\"invalid\"}]")]
    [InlineData("[{\"id\":\"1\",\"content\":\"a\"},{\"id\":\"1\",\"content\":\"b\"}]")]
    [InlineData("[{\"id\":\"1\",\"content\":\" \"}]")]
    [InlineData("[null]")]
    public void InvalidUpdatesPreservePreviousList(string json)
    {
        using var store = new DataStore(_root); var session = new Conversation(); store.AddConversation(session);
        store.SetTodos(session.Id, [new("original", "保留")]);
        Assert.Throws<ArgumentException>(() => store.SetTodos(session.Id, JsonSerializer.Deserialize<TodoItem[]>(json, ToolResult.JsonOptions)!));
        Assert.Equal("original", Assert.Single(store.GetTodos(session.Id)).Id);
    }

    [Fact]
    public void ParallelItemsMayBeInProgressAtTheSameTime()
    {
        using var store = new DataStore(_root); var session = new Conversation(); store.AddConversation(session);
        var saved = store.SetTodos(session.Id, [new("a", "子代理 A", "in_progress"), new("b", "子代理 B", "in_progress"), new("c", "收尾", "pending")]);
        Assert.Equal(2, saved.Count(item => item.Status == "in_progress"));
        Assert.Equal(2, store.GetTodos(session.Id).Count(item => item.Status == "in_progress"));
    }

    [Fact]
    public async Task ToolsAreScopedAndRejectCrossSessionTargetsAndCancellation()
    {
        using var store = new DataStore(_root); var parent = new Conversation(); var child = new Conversation { ParentConversationId = parent.Id };
        store.AddConversation(parent); store.AddConversation(child);
        var tools = new ToolRegistry(); tools.Register(new TodoTools(store));
        var write = JsonSerializer.SerializeToElement(new { todos = new[] { new TodoItem("1", "当前会话") } }, ToolResult.JsonOptions);
        Assert.False((await tools.CallAsync("todo_read", JsonSerializer.SerializeToElement(new { }), default)).Ok);
        using (AgentExecutionScope.Enter(false, parent.Id))
        {
            Assert.True((await tools.CallAsync("todo_write", write, default)).Ok);
            Assert.False((await tools.CallAsync("todo_read", JsonSerializer.SerializeToElement(new { conversationId = child.Id }), default)).Ok);
        }
        using (AgentExecutionScope.Enter(true, child.Id))
        {
            var read = await tools.CallAsync("todo_read", JsonSerializer.SerializeToElement(new { }), default);
            Assert.Empty(JsonSerializer.SerializeToElement(read.Data, ToolResult.JsonOptions).GetProperty("todos").EnumerateArray());
            Assert.True((await tools.CallAsync("todo_write", write, default)).Ok);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tools.CallAsync("todo_write", write, cancelled.Token));
        }
        Assert.Single(store.GetTodos(parent.Id)); Assert.Single(store.GetTodos(child.Id));
    }

    [Fact]
    public void CommandsUpdateProgressAndRenderTextSafely()
    {
        using var store = new DataStore(_root); var session = new Conversation(); store.AddConversation(session);
        var ui = new TerminalUi(true, false);
        TodoCommand.Execute(store, ui, session.Id, ["add", "第一步"]);
        TodoCommand.Execute(store, ui, session.Id, ["add", "第二步"]);
        TodoCommand.Execute(store, ui, session.Id, ["start", "1"]);
        TodoCommand.Execute(store, ui, session.Id, ["start", "2"]);
        Assert.Equal("in_progress", store.GetTodos(session.Id)[0].Status);
        TodoCommand.Execute(store, ui, session.Id, ["done", "2"]);
        var result = JsonSerializer.SerializeToElement(new { data = new { todos = store.GetTodos(session.Id) } }, ToolResult.JsonOptions);
        Assert.Contains("已完成 · 第二步", TerminalUi.ResultSummary(result, false, "todo_write"));
        Assert.Throws<ArgumentException>(() => TodoCommand.Execute(store, ui, session.Id, ["done", "3"]));
    }
}
