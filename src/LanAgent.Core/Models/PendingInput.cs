namespace LanAgent.Core.Models;

/// <summary>A persisted user prompt waiting for an idle or model step boundary.</summary>
public sealed record PendingInput(Guid Id, Guid ConversationId, string Draft, string Content,
    IReadOnlyList<ImageAttachment> Images, string Delivery, DateTime CreatedAt);
