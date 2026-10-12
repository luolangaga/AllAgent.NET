using System;

namespace LanAgent.Core.Models;

public class Message
{
    public List<ImageAttachment> Images { get; set; } = new();
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = ""; // "user" | "assistant" | "tool"
    public string Content { get; set; } = "";
    public string? InputText { get; set; } // Human draft before host file-reference expansion.
    public bool IsInternal { get; set; } // Host continuation instructions are model history, not user-visible chat.
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? ReasoningContent { get; set; } // thinking/reasoning text (deepseek, claude thinking, etc.)
    public string? ProviderStateJson { get; set; }
    public string? ProviderContextKey { get; set; } // State ownership across model/provider changes and restarts.
    public string? HostToolTraceJson { get; set; } // UI execution trace only; never added to provider history.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TokenCount { get; set; }
}
