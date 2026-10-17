using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Bridge;

public sealed record ApprovalResponse(string RequestId, bool Approved);
public sealed record QuestionAnswer(string Id, string? Answer);

/// <summary>
/// Routes host approvals and agent questions to connected browser clients.
/// The desktop build resolves these with native dialogs; a browser host (the
/// terminal's /web-ui) wires this broker in so a run never stalls waiting on
/// a prompt the person cannot see.
/// </summary>
public sealed class WebApprovals
{
    private readonly BridgeEventPusher _pusher;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _questions = new();
    /// <summary>Prompts still waiting for an answer, kept so a reloaded page can be handed them again.</summary>
    private readonly ConcurrentDictionary<string, object> _pending = new();

    /// <summary>Live permission-mode probes so terminal commands like /plan and /permissions stay authoritative.</summary>
    public Func<bool>? PlanMode { get; set; }
    public Func<bool>? FullAccess { get; set; }
    /// <summary>Answers whether a browser client is actually connected; without one a pending prompt is denied instead of hanging.</summary>
    public Func<bool>? HasClients { get; set; }
    public WorkspaceCheckpoints? Checkpoints { get; set; }

    public WebApprovals(BridgeDispatcher dispatcher, BridgeEventPusher pusher)
    {
        _pusher = pusher;
        dispatcher.Register<ApprovalResponse, bool>("agent.approvalRespond", (response, ct) =>
        {
            var found = _approvals.TryRemove(response.RequestId, out var completion);
            if (found) { _pending.TryRemove(response.RequestId, out _); completion!.TrySetResult(response.Approved); }
            return Task.FromResult(found);
        });
        dispatcher.Register<QuestionAnswer, bool>("agent.questionAnswer", (answer, ct) =>
        {
            var found = _questions.TryRemove(answer.Id, out var completion);
            if (found) { _pending.TryRemove(answer.Id, out _); completion!.TrySetResult(answer.Answer); }
            return Task.FromResult(found);
        });
        dispatcher.Register<IReadOnlyList<object>>("interactions.pending", _ => Task.FromResult(PendingPrompts()));
    }

    public async ValueTask<bool> ApproveAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var planMode = PlanMode?.Invoke() == true;
        if (planMode && request.Risk != ToolRisk.Read) return false;
        if (planMode && request.Name == "external_directory" && !IsReadOnlyDirectoryRequest(request.Arguments)) return false;
        if (FullAccess?.Invoke() == true && !request.RequiresExplicitApproval)
        {
            Checkpoints?.MarkExternalAction();
            return true;
        }
        if (request.Preapproved && !request.RequiresExplicitApproval) return true;
        if (HasClients is { } hasClients && !hasClients()) return false;

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[requestId] = completion;
        var payload = new
        {
            type = "agent.approvalRequest",
            requestId,
            name = request.Name,
            risk = request.Risk.ToString(),
            args = request.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : request.Arguments.GetRawText(),
            source = request.Source,
            requiresExplicitApproval = request.RequiresExplicitApproval,
        };
        _pending[requestId] = payload;
        Push(payload);
        try
        {
            var approved = await completion.Task.WaitAsync(ct);
            if (approved && (request.Name.StartsWith("mcp_", StringComparison.Ordinal) || request.Name.StartsWith("computer_", StringComparison.Ordinal)))
                Checkpoints?.MarkExternalAction();
            return approved;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _approvals.TryRemove(requestId, out _);
            _pending.TryRemove(requestId, out _);
            Push(new { type = "interactions.resolved", id = requestId });
        }
    }

    public async Task<string?> AskAsync(AgentQuestion question, CancellationToken ct)
    {
        if (HasClients is { } hasClients && !hasClients()) return null;
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[id] = completion;
        var payload = new { type = "agent.question", id, question = question.Question, choices = question.Choices, multiple = question.Multiple };
        _pending[id] = payload;
        Push(payload);
        try
        {
            return await completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _questions.TryRemove(id, out _);
            _pending.TryRemove(id, out _);
            Push(new { type = "interactions.resolved", id });
        }
    }

    /// <summary>
    /// Re-sends every prompt that is still waiting for an answer. A blocked run survives a page
    /// reload, a reconnect or a restarted browser as long as the host replays these on connect.
    /// </summary>
    public int ReplayPending()
    {
        var prompts = _pending.Values.ToArray();
        foreach (var prompt in prompts) Push(prompt);
        return prompts.Length;
    }

    /// <summary>Prompts that are still waiting for an answer, so a loaded page can ask for them.</summary>
    public IReadOnlyList<object> PendingPrompts() => _pending.Values.ToArray();

    private static bool IsReadOnlyDirectoryRequest(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("operation", out var operation)) return false;
        return operation.GetString() is "file_read" or "file_search" or "everything_search" or "list_directory";
    }

    private void Push(object payload) => _pusher.Push(JsonSerializer.Serialize(payload, JsonOptions.Default));
}
