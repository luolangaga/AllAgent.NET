using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Data;

public partial class DataStore
{
    private long _plansRevision;

    /// <summary>Bumped whenever a session's plan changes, so live views can redraw immediately.</summary>
    public long PlansRevision => Interlocked.Read(ref _plansRevision);

    public PlanDocument? GetPlan(Guid conversationId)
    {
        _lock.EnterReadLock();
        try { return (_data.Conversations.FirstOrDefault(c => c.Id == conversationId)
            ?? throw new ArgumentException("Conversation not found.")).Plan; }
        finally { _lock.ExitReadLock(); }
    }

    public PlanDocument SetPlan(Guid conversationId, PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.Id) || plan.Id.Length > 64 ||
            string.IsNullOrWhiteSpace(plan.Title) || plan.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(plan.Markdown) || plan.Markdown.Length > 64_000 ||
            plan.Status is not ("proposed" or "approved" or "rejected"))
            throw new ArgumentException("计划 ID 为 1–64 字符，标题为 1–200 字符，Markdown 为 1–64000 字符，状态为 proposed、approved 或 rejected。");
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var conversation = _data.Conversations.FirstOrDefault(c => c.Id == conversationId)
                ?? throw new ArgumentException("Conversation not found.");
            conversation.Plan = plan;
            conversation.UpdatedAt = DateTime.UtcNow;
            Interlocked.Increment(ref _plansRevision);
            SaveAndVerify(conversationId);
            return plan;
        }
        finally { _lock.ExitWriteLock(); }
    }
}
