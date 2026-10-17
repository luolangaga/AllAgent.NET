using System;

namespace AllAgent.NET.Core.Models;

public class Message
{
    public List<ImageAttachment> Images { get; set; } = new();
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = ""; // "user" | "assistant" | "tool"
    public string Content { get; set; } = "";
    public string? InputText { get; set; } // Human draft before host file-reference expansion.
    public bool IsInternal { get; set; } // Host continuation instructions are model history, not user-visible chat.
    public bool IsHostActivity { get; set; } // UI-only activity (e.g. learned memory), excluded from model history.
    public bool IsInterrupted { get; set; } // Visible partial response; never contains executable calls or native state.
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? ReasoningContent { get; set; } // thinking/reasoning text (deepseek, claude thinking, etc.)
    public string? ProviderStateJson { get; set; }
    public string? ProviderContextKey { get; set; } // State ownership across model/provider changes and restarts.
    public string? HostToolTraceJson { get; set; } // UI execution trace only; never added to provider history.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TokenCount { get; set; }

    public string GetContextContent() => IsInterrupted
        ? Content + "\n[This response was interrupted before completion; no tool calls from this partial response were executed.]" : Content;
}
