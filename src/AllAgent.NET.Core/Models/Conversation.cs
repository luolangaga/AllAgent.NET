using System;
using System.Collections.Generic;

namespace AllAgent.NET.Core.Models;

public class Conversation
{
    public string? WorkspaceRoot { get; set; }
    public Guid? ParentConversationId { get; set; }
    public string? AgentRole { get; set; }
    public Guid? SubAgentConfigId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? SystemPrompt { get; set; }
    public string ModelId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? ContextSummary { get; set; }
    public Guid? ContextSummaryThroughId { get; set; }
    public int? ContextUsedTokens { get; set; }
    public bool ContextUsageEstimated { get; set; } = true;
    public string? ContextUsageModel { get; set; }
    public long CacheInputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long CacheUsageRequests { get; set; }
    public List<Message> Messages { get; set; } = new();
    public List<TodoItem> Todos { get; set; } = new();
    public SessionRevertState? Revert { get; set; }
    public List<Guid> InvalidatedCheckpoints { get; set; } = new();
}
