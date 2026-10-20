using System.ComponentModel;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

/// <summary>Host-owned plan approval. The tool only works while the host is in plan mode and
/// returns a small decision to the model; the plan itself is persisted with the conversation.</summary>
public partial class PlanTools(
    DataStore? store,
    Func<PlanProposal, CancellationToken, Task<PlanDecision>>? handler,
    Func<bool>? planMode) : IAgentToolSet
{
    private const int MaximumMarkdownCharacters = 64_000;

    [AgentTool("plan_propose", Risk = ToolRisk.Read)]
    [Description("Present a complete implementation plan to the human and wait for approval. Only available in plan mode. Write the full plan in Markdown (goal, context, affected files, ordered steps, risks and verification). The call blocks until the human approves or rejects. The decision may carry a human comment: when rejected, revise the plan from that comment and propose it again; when approved, honor the comment while executing. If approved, full auto-approval is active: execute the approved plan immediately and do not propose it again.")]
    public async Task<ToolResult> ProposeAsync(
        [Description("Plan title, 1–200 characters")] string title,
        [Description("Complete plan in Markdown, 1–64000 characters")] string markdown,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentExecutionScope.IsSubAgent)
            return ToolResult.Failure("Delegated agents cannot ask the human to approve a plan. Return findings or blockers to the parent agent.");
        if (planMode?.Invoke() != true)
            return ToolResult.Failure("plan_propose is only available in plan mode; do not call it from another permission mode.");
        if (handler == null)
            return ToolResult.Failure("Plan approval is unavailable in this host. Stop and report the blocker instead of executing.");
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
            return ToolResult.Failure("The plan title must contain 1–200 characters.");
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Length > MaximumMarkdownCharacters)
            return ToolResult.Failure($"The plan Markdown must contain 1–{MaximumMarkdownCharacters} characters.");
        if (store == null || AgentExecutionScope.ConversationId is not { } conversationId)
            return ToolResult.Failure("Plan tools require an active conversation.");

        var document = new PlanDocument(Guid.NewGuid().ToString("N"), title.Trim(), markdown,
            "proposed", null, DateTime.UtcNow, null);
        var proposal = new PlanProposal(document.Title, document.Markdown, document.Id);
        store.SetPlan(conversationId, document);

        PlanDecision decision;
        try
        {
            decision = await handler(proposal, ct);
        }
        catch (OperationCanceledException)
        {
            store.SetPlan(conversationId, document with
            {
                Status = "rejected",
                Feedback = "Plan approval was cancelled; stay in plan mode.",
                DecidedAt = DateTime.UtcNow
            });
            throw;
        }
        catch (Exception ex)
        {
            store.SetPlan(conversationId, document with
            {
                Status = "rejected",
                Feedback = "Plan approval failed: " + ex.Message,
                DecidedAt = DateTime.UtcNow
            });
            throw;
        }

        var approved = decision.Approved;
        var feedback = string.IsNullOrWhiteSpace(decision.Feedback) ? null : decision.Feedback.Trim();
        store.SetPlan(conversationId, document with
        {
            Status = approved ? "approved" : "rejected",
            Feedback = feedback,
            DecidedAt = DateTime.UtcNow
        });
        return ToolResult.Success(new
        {
            approved,
            mode = approved ? decision.Mode ?? "full" : null,
            feedback,
            message = approved
                ? feedback == null
                    ? "Plan approved. Full auto-approval is active. Execute the approved plan now; do not call plan_propose again for this plan."
                    : "Plan approved with the human's comment. Full auto-approval is active. Execute the approved plan now and satisfy that comment; do not call plan_propose again for this plan."
                : feedback == null
                    ? "Plan rejected. Stay in plan mode, revise the plan, and call plan_propose again when it is ready."
                    : "Plan rejected with the human's comment. Stay in plan mode, revise the plan to address that comment, and call plan_propose again when it is ready."
        });
    }
}
