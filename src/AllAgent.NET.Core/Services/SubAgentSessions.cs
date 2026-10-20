using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services;

public sealed record SubAgentRequest(string Agent, string Description, string Prompt, string? Model = null, string? Provider = null, Guid? SessionId = null, bool Background = false)
{
    public SubAgentTaskContract? Contract { get; init; }
}

/// <summary>A launched child job; Joined means the request steered a running child instead of starting a new run.</summary>
public sealed record SubAgentLaunch(string TaskId, bool Joined = false);

/// <summary>Shared mapping from child job state to parent-facing exit codes and partial marking.</summary>
public static class SubAgentOutcome
{
    /// <summary>0 completed, 130 cancelled, 1 failed/interrupted; null while the child has not reached a final state.</summary>
    public static int? ExitCode(BackgroundTaskState state) => state switch
    {
        BackgroundTaskState.Completed => 0,
        BackgroundTaskState.Cancelled => 130,
        BackgroundTaskState.Failed or BackgroundTaskState.Interrupted => 1,
        _ => null
    };
    /// <summary>Any non-final state and any result that is not a completed answer is partial evidence.</summary>
    public static bool IsPartial(BackgroundTaskState state) => state != BackgroundTaskState.Completed;
    public static bool IsFinal(BackgroundTaskState state) => state is BackgroundTaskState.Completed or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled or BackgroundTaskState.Interrupted;
}

/// <summary>Validates child ownership before hosts admit a new delegated turn.</summary>
public static class SubAgentSessions
{
    public static Conversation Resolve(DataStore store, BackgroundTasks tasks, Guid parentId, SubAgentRequest request, bool allowRunning = false, AgentRoleCatalog? roles = null)
    {
        var role = (roles ?? new AgentRoleCatalog(store)).Get(request.Agent);
        var previous = request.SessionId is { } previousId ? store.GetConversation(previousId) : null;
        // Legacy text keeps its foreground/background lifetime, but it does not implicitly
        // authorize file writes. A continuation retains the parent's previously stated scope.
        var effectiveContract = request.Contract ?? previous?.Delegation ?? new SubAgentTaskContract(request.Prompt is { Length: > 100_000 } longTask ? longTask[..100_000] : request.Prompt) { RequiredForCompletion = !request.Background };
        effectiveContract.Validate();
        effectiveContract = effectiveContract.Snapshot();
        if (role.Name == "modify" && effectiveContract.EditablePaths.Count == 0)
            throw new ArgumentException("Modification tasks require explicit editablePaths in the task contract.");
        if (effectiveContract is { } contract)
            foreach (var job in tasks.List.Where(job => job.ParentSessionId == parentId && BackgroundTasks.IsActive(job.State) && job.SessionId != request.SessionId))
                if (store.GetConversation(job.SessionId)?.Delegation is { } active && contract.EditablePaths.Any(path => active.EditablePaths.Any(other => SubAgentTaskContract.Overlaps(path, other))))
                    throw new InvalidOperationException("Delegated edit scope overlaps an active task. Wait for that task before launching another writer.");
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 1_000_000) throw new ArgumentException("Prompt must be nonempty and within the 1,000,000-character host transport budget.");
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 120) throw new ArgumentException("Description must be 1–120 characters.");
        if (request.Model?.Length > 256 || request.Provider?.Length > 256) throw new ArgumentException("Model and provider selectors must not exceed 256 characters.");
        if (store.GetConversation(parentId) == null) throw new ArgumentException("Parent conversation not found.");
        if (request.SessionId is { } id)
        {
            var child = store.GetConversation(id) ?? throw new ArgumentException("Subagent session not found.");
            if (child.ParentConversationId != parentId) throw new ArgumentException("Session is not a child of the current conversation.");
            if (tasks.List.Any(j => j.SessionId == id && BackgroundTasks.IsActive(j.State)))
            {
                if (!allowRunning) throw new InvalidOperationException("This subagent is already running. Wait for completion before continuing its conversation.");
                return child;
            }
            child.AgentRole = request.Agent;
            child.Delegation = effectiveContract;
            return child;
        }
        return new Conversation { ParentConversationId = parentId, WorkspaceRoot = store.GetConversation(parentId)?.WorkspaceRoot, AgentRole = request.Agent, Title = request.Description, Delegation = effectiveContract };
    }
}
