using LanAgent.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanAgent.Core.Data;

public partial class DataStore
{
    public List<Message> GetVisibleMessages(Guid id)
    {
        _lock.EnterReadLock();
        try
        {
            var conversation = _data.Conversations.SingleOrDefault(c => c.Id == id);
            if (conversation == null) return [];
            var messages = conversation.Messages.OrderBy(m => m.CreatedAt).ToList();
            if (conversation.Revert is not { } revert) return messages;
            var index = messages.FindIndex(m => m.Id == revert.MessageId);
            if (index < 0) throw new InvalidOperationException("撤回边界已丢失，未读取不完整的上下文。");
            return messages.Take(index).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }
    public void CommitRevert(Guid id, string workspace)
    {
        _lock.EnterWriteLock();
        try
        {
            var conversation = _data.Conversations.Single(c => c.Id == id);
            if (conversation.Revert is not { } revert) return;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), revert.Workspace, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请返回撤回操作所在的工作区再继续任务。");
            var boundary = conversation.Messages.FindIndex(m => m.Id == revert.MessageId);
            if (boundary < 0) throw new InvalidOperationException("撤回边界已丢失。");
            conversation.Messages = conversation.Messages.Take(boundary).ToList(); conversation.Revert = null;
            conversation.InvalidatedCheckpoints = conversation.InvalidatedCheckpoints.Concat(revert.Checkpoints).Distinct().ToList();
            if (conversation.ContextSummaryThroughId is { } through && !conversation.Messages.Any(m => m.Id == through))
            { conversation.ContextSummary = null; conversation.ContextSummaryThroughId = null; }
            conversation.ContextUsedTokens = null; conversation.UpdatedAt = DateTime.UtcNow; SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }
    internal static string RevertHistoryHash(IEnumerable<Message> messages) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(messages))));
    internal (SessionRevertState? State, string HistoryHash) PersistedRevert(Guid id)
    {
        _lock.EnterReadLock();
        try
        {
            if (!TryLoadFile(_filePath, out var persisted) || persisted == null) throw new IOException("无法核对撤回事务的持久状态。");
            var conversation = persisted.Conversations.Single(c => c.Id == id);
            return (conversation.Revert, RevertHistoryHash(conversation.Messages));
        }
        finally { _lock.ExitReadLock(); }
    }
}
