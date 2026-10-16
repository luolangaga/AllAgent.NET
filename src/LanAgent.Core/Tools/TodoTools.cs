using System.ComponentModel;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.Tools;

/// <summary>Session metadata only; never accepts a target session from the model.</summary>
public partial class TodoTools(DataStore store) : IAgentToolSet
{
    [AgentTool("todo_read")]
    [Description("Read the current conversation's persistent TODO checklist.")]
    public Task<ToolResult> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentExecutionScope.ConversationId is not { } id)
            return Task.FromResult(ToolResult.Failure("TODO tools require an active conversation."));
        return Task.FromResult(ToolResult.Success(new { todos = store.GetTodos(id) }));
    }

    // Updating the agent's own progress metadata needs no filesystem approval.
    [AgentTool("todo_write")]
    [Description("Replace the current conversation's TODO checklist (at most 100 items). Use stable unique IDs, retain unfinished items, and update progress as work happens. At most one in_progress item. Empty todos clears the list. This records progress, it does not execute tasks.")]
    public Task<ToolResult> WriteAsync([Description("Complete replacement checklist; statuses: pending, in_progress, completed")] TodoEntry[] todos, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentExecutionScope.ConversationId is not { } id)
            return Task.FromResult(ToolResult.Failure("TODO tools require an active conversation."));
        if (todos == null || todos.Any(t => t == null)) throw new ArgumentException("todos must be an array of TODO entries.");
        return Task.FromResult(ToolResult.Success(new { todos = store.SetTodos(id,
            todos.Select(t => new TodoItem(t.id, t.content, t.status)).ToArray()) }));
    }
}

public sealed record TodoEntry(
    [property: Description("Stable unique ID, 1–64 characters")] string id,
    [property: Description("Task description, 1–300 characters")] string content,
    [property: Description("Exactly pending, in_progress, or completed")] string status);
