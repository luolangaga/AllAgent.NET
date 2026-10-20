namespace AllAgent.NET.Core.Models;

/// <summary>A plan the agent proposes while the host is in plan mode. Markdown is the single
/// content source: the terminal renders it directly and the Web UI renders it to HTML.</summary>
public sealed record PlanProposal(string Title, string Markdown, string? Id = null);

/// <summary>The human's decision on a proposed plan. Mode is the permission mode the host
/// enabled when approving (currently "full").</summary>
public sealed record PlanDecision(bool Approved, string? Feedback = null, string? Mode = null);

/// <summary>The latest plan stored with a conversation, including the decision state.</summary>
public sealed record PlanDocument(
    string Id,
    string Title,
    string Markdown,
    string Status,
    string? Feedback,
    DateTime CreatedAt,
    DateTime? DecidedAt);
