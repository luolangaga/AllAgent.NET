using LanAgent.Core.Data;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services;

public sealed record SubAgentRequest(string Agent, string Description, string Prompt, string? Model = null, string? Provider = null, Guid? SessionId = null);

/// <summary>Validates child ownership before hosts admit a new delegated turn.</summary>
public static class SubAgentSessions
{
    public static Conversation Resolve(DataStore store, BackgroundTasks tasks, Guid parentId, SubAgentRequest request, bool allowRunning = false, AgentRoleCatalog? roles = null)
    {
        (roles ?? new AgentRoleCatalog(store)).Get(request.Agent);
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 12000) throw new ArgumentException("Prompt must be 1–12000 characters.");
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 120) throw new ArgumentException("Description must be 1–120 characters.");
        if (store.GetConversation(parentId) == null) throw new ArgumentException("Parent conversation not found.");
        if (request.SessionId is { } id)
        {
            var child = store.GetConversation(id) ?? throw new ArgumentException("Subagent session not found.");
            if (child.ParentConversationId != parentId) throw new ArgumentException("Session is not a child of the current conversation.");
            if (tasks.List.Any(j => j.SessionId == id && j.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval))
            {
                if (!allowRunning) throw new InvalidOperationException("This subagent is already running. Wait for completion before continuing its conversation.");
                return child;
            }
            child.AgentRole = request.Agent;
            return child;
        }
        return new Conversation { ParentConversationId = parentId, AgentRole = request.Agent, Title = request.Description };
    }
}
