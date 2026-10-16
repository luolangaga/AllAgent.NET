using LanAgent.Core.Models;

namespace LanAgent.Core.Data;

public partial class DataStore
{
    public IReadOnlyList<TodoItem> GetTodos(Guid conversationId)
    {
        _lock.EnterReadLock();
        try { return (_data.Conversations.FirstOrDefault(c => c.Id == conversationId)
            ?? throw new ArgumentException("Conversation not found.")).Todos.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<TodoItem> SetTodos(Guid conversationId, IReadOnlyList<TodoItem> todos)
    {
        ArgumentNullException.ThrowIfNull(todos);
        var snapshot = todos.ToList();
        if (snapshot.Count > 100 || snapshot.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 64 ||
            string.IsNullOrWhiteSpace(t.Content) || t.Content.Length > 300 || t.Status is not ("pending" or "in_progress" or "completed")))
            throw new ArgumentException("TODO 最多 100 项；ID 为 1–64 字符，内容为 1–300 字符，状态为 pending、in_progress 或 completed。");
        if (snapshot.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Count)
            throw new ArgumentException("TODO ID 不能重复。");
        if (snapshot.Count(t => t.Status == "in_progress") > 1)
            throw new ArgumentException("同时只能有一项 TODO 处于进行中。");
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var conversation = _data.Conversations.FirstOrDefault(c => c.Id == conversationId)
                ?? throw new ArgumentException("Conversation not found.");
            if (conversation.Todos.SequenceEqual(snapshot)) return snapshot.ToArray();
            conversation.Todos = snapshot;
            conversation.UpdatedAt = DateTime.UtcNow;
            SaveAndVerify(conversationId);
            return snapshot.ToArray();
        }
        finally { _lock.ExitWriteLock(); }
    }
}
