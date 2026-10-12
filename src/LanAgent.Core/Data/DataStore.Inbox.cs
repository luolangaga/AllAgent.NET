using LanAgent.Core.Models;

namespace LanAgent.Core.Data;

public partial class DataStore
{
    public IReadOnlyList<PendingInput> GetPendingInputs(Guid session)
    {
        _lock.EnterReadLock();
        try { return _data.PendingInputs.Where(item => item.ConversationId == session)
            .Select(item => item with { Images = item.Images.ToArray() }).ToArray(); }
        finally { _lock.ExitReadLock(); }
    }
    public PendingInput EnqueueInput(Guid session, string draft, string content, IReadOnlyList<ImageAttachment> images, string delivery = "queue", ApiConfig? subAgentConfig = null, string? subAgentRole = null)
    {
        ValidateDelivery(delivery); ImageAttachment.Validate(images);
        if (subAgentRole != null) new LanAgent.Core.Services.AgentRoleCatalog(this).Get(subAgentRole);
        if (string.IsNullOrWhiteSpace(content) || content.Length > 100000 || draft.Length > 32000)
            throw new ArgumentException("待处理输入为空或超出长度上限。");
        _lock.EnterWriteLock();
        try
        {
            var conversation = _data.Conversations.SingleOrDefault(c => c.Id == session) ?? throw new ArgumentException("会话不存在。");
            if (subAgentConfig != null && (conversation.ParentConversationId == null || !subAgentConfig.IsEnabled || string.IsNullOrWhiteSpace(subAgentConfig.DefaultModel)))
                throw new ArgumentException("模型切换只允许具有有效配置的子会话。");
            if (subAgentRole != null && conversation.ParentConversationId == null) throw new ArgumentException("子会话角色无效。");
            if (_data.PendingInputs.Count(item => item.ConversationId == session) >= 100) throw new InvalidOperationException("每个会话最多保留 100 条待处理输入。");
            var item = new PendingInput(Guid.NewGuid(), session, draft, content, images.ToArray(), delivery, DateTime.UtcNow);
            _data.PendingInputs.Add(item);
            if (subAgentRole != null) { conversation.AgentRole = subAgentRole; conversation.UpdatedAt = DateTime.UtcNow; }
            if (subAgentConfig != null)
            {
                conversation.SubAgentConfigId = subAgentConfig.Id; conversation.ModelId = subAgentConfig.DefaultModel;
                conversation.ProviderId = subAgentConfig.ProviderId; conversation.UpdatedAt = DateTime.UtcNow;
            }
            SaveAndVerify(); return item with { Images = item.Images.ToArray() };
        }
        finally { _lock.ExitWriteLock(); }
    }
    public void UpdatePendingInput(Guid session, Guid input, string delivery)
    {
        ValidateDelivery(delivery); _lock.EnterWriteLock();
        try
        {
            var index = _data.PendingInputs.FindIndex(item => item.ConversationId == session && item.Id == input);
            if (index < 0) throw new InvalidOperationException("输入已经被处理或不属于当前会话。");
            _data.PendingInputs[index] = _data.PendingInputs[index] with { Delivery = delivery }; SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }
    public PendingInput CancelPendingInput(Guid session, Guid input)
    {
        _lock.EnterWriteLock();
        try
        {
            var item = _data.PendingInputs.SingleOrDefault(item => item.ConversationId == session && item.Id == input)
                ?? throw new InvalidOperationException("输入已经被处理或不属于当前会话。");
            _data.PendingInputs.Remove(item); SaveAndVerify(); return item;
        }
        finally { _lock.ExitWriteLock(); }
    }
    /// <summary>All steers at a step boundary; otherwise one queued prompt at an idle boundary.
    /// Removing pending items and admitting user messages share one atomic datastore write.</summary>
    public IReadOnlyList<Message> PromotePendingInputs(Guid session, bool steersOnly)
    {
        _lock.EnterWriteLock();
        try
        {
            var conversation = _data.Conversations.SingleOrDefault(c => c.Id == session) ?? throw new ArgumentException("会话不存在。");
            var items = _data.PendingInputs.Where(item => item.ConversationId == session && item.Delivery == "steer").ToArray();
            if (items.Length == 0 && !steersOnly) items = _data.PendingInputs.Where(item => item.ConversationId == session && item.Delivery == "queue").Take(1).ToArray();
            if (items.Length == 0) return [];
            var messages = items.Select(item => new Message { Id = item.Id, ConversationId = session,
                Role = "user", Content = item.Content, InputText = item.Draft, Images = item.Images.ToList(), CreatedAt = DateTime.UtcNow }).ToArray();
            foreach (var item in items) _data.PendingInputs.Remove(item);
            conversation.Messages.AddRange(messages); conversation.UpdatedAt = DateTime.UtcNow;
            SaveAndVerify(); return messages;
        }
        finally { _lock.ExitWriteLock(); }
    }
    private static void ValidateDelivery(string delivery)
    {
        if (delivery is not ("queue" or "steer")) throw new ArgumentException("delivery 必须为 queue 或 steer。");
    }
}
