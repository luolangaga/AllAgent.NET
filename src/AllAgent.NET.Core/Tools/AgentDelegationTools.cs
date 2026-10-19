using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Tools;

/// <summary>Model-facing delegation: foreground by default, explicit background with completion notifications.</summary>
public sealed class AgentDelegationTools(BackgroundTasks tasks, Func<SubAgentRequest, CancellationToken, Task<SubAgentLaunch>> launch) : IAgentToolSet
{
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"properties":{
          "agent":{"type":"string","description":"Configured agent name for single mode"},
          "task":{"type":"string","description":"Self-contained delegated task for single mode"},
          "description":{"type":"string","description":"Short task label, 1–120 characters; defaults to the task preview"},
          "sessionID":{"type":"string","description":"Previous child conversation ID; continues a finished child or steers a running background child"},
          "background":{"type":"boolean","description":"Single mode only: return immediately; the final result arrives later as a completion notification"},
          "model":{"type":"string","description":"Optional user-requested model ID"},
          "provider":{"type":"string","description":"Optional user-requested configured provider"},
          "tasks":{"type":"array","maxItems":8,"description":"Parallel tasks; at most 8, concurrency 6","items":{"type":"object","additionalProperties":false,"properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}},
          "chain":{"type":"array","description":"Sequential tasks; {previous} in task inserts the preceding result","items":{"type":"object","additionalProperties":false,"properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}}
        }}
        """);
    private static readonly JsonElement CancelSchema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","description":"Delegated task ID"}},"required":["id"]}
        """);
    public IReadOnlyList<AgentToolDescriptor> Definitions { get; } =
    [
        new("subagent",
            "Delegate a bounded independent task to a configured child agent. Provide exactly one mode: agent+task (single), tasks (parallel, max 8/concurrency 6) or chain (sequential, {previous} inserts the prior result). Foreground returns results inline; background=true returns a taskId and notifies later; sessionID steers an existing child. Children inherit permissions, share the workspace and cannot ask the user or delegate. partial=true means unfinished — never present it as done. Set model/provider only when the user asked for it.",
            Schema, ToolRisk.Read),
        new("cancel_agent",
            "Cancel a delegated child and its running sandbox. A cancelled child keeps partial output only (partial=true) and cannot accept further steers. Already produced external side effects are not rolled back.",
            CancelSchema, ToolRisk.Read)
    ];

    public async ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (name == "cancel_agent") return Cancel(arguments);
        if (name != "subagent") return ToolResult.Failure("Unknown tool: " + name);
        if (AgentExecutionScope.IsSubAgent) return ToolResult.Failure("Delegated agents cannot delegate further tasks.");
        ToolArguments.Validate(arguments, Schema);
        var single = arguments.TryGetProperty("agent", out _) || arguments.TryGetProperty("task", out _);
        var parallel = arguments.TryGetProperty("tasks", out var parallelItems);
        var chain = arguments.TryGetProperty("chain", out var chainItems);
        if ((single ? 1 : 0) + (parallel ? 1 : 0) + (chain ? 1 : 0) != 1)
            return ToolResult.Failure("Provide exactly one mode: agent + task, tasks, or chain.");
        if (!single && (arguments.TryGetProperty("background", out _) || arguments.TryGetProperty("sessionID", out _) ||
            arguments.TryGetProperty("model", out _) || arguments.TryGetProperty("provider", out _) || arguments.TryGetProperty("description", out _)))
            return ToolResult.Failure("background, sessionID, model, provider and description apply to single mode only.");
        var items = single ? new[] { arguments } : (parallel ? parallelItems : chainItems).EnumerateArray().ToArray();
        if (items.Length == 0 || parallel && items.Length > 8) return ToolResult.Failure("Tasks must be nonempty; parallel mode supports at most 8 tasks.");
        // Validate the entire batch before launching its first side effect.
        foreach (var item in items)
        {
            var agent = ToolArguments.Get<string>(item, "agent", "");
            var task = ToolArguments.Get<string>(item, "task", "");
            if (string.IsNullOrWhiteSpace(agent) || agent.Length > 120 || string.IsNullOrWhiteSpace(task) || task.Length > 1_000_000)
                return ToolResult.Failure("Each task needs agent (1–120 characters) and nonempty task within the host's 1,000,000-character transport budget.");
        }
        Guid? session = null;
        var description = ToolArguments.Get<string>(arguments, "description", "") ?? "";
        var model = ToolArguments.Get<string>(arguments, "model", "") ?? "";
        var provider = ToolArguments.Get<string>(arguments, "provider", "") ?? "";
        var background = ToolArguments.Get<bool>(arguments, "background", false);
        if (single)
        {
            if (ToolArguments.Get<string>(arguments, "sessionID", "") is { Length: > 0 } sessionText)
            {
                if (!Guid.TryParse(sessionText, out var parsed)) return ToolResult.Failure("Invalid subagent sessionID.");
                session = parsed;
            }
            if (description.Length > 120) return ToolResult.Failure("Description must be 1–120 characters.");
            if (model.Length > 256 || provider.Length > 256) return ToolResult.Failure("Model and provider selectors must not exceed 256 characters.");
        }
        if (single && background)
        {
            var agent = ToolArguments.Get<string>(arguments, "agent", "");
            var task = ToolArguments.Get<string>(arguments, "task", "");
            try
            {
                var launched = await launch(new(agent, description.Length > 0 ? description : agent + " · " + Title(task), task,
                    model.Length > 0 ? model : null, provider.Length > 0 ? provider : null, session, Background: true), ct);
                var info = tasks.Get(launched.TaskId);
                return ToolResult.Success(new { mode = "background", taskId = launched.TaskId, sessionID = info.SessionId, status = info.State.ToString() });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ToolResult.Failure(ex.Message); }
        }
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = new System.Collections.Concurrent.ConcurrentBag<string>();
        var results = new DelegationResult[items.Length];
        try
        {
            if (parallel)
            {
                using var slots = new SemaphoreSlim(6);
                await Task.WhenAll(items.Select(async (item, index) =>
                {
                    await slots.WaitAsync(batch.Token);
                    try { results[index] = await Run(item, index, null); }
                    finally { slots.Release(); }
                }));
            }
            else
            {
                string? previous = null;
                for (var index = 0; index < items.Length; index++)
                {
                    results[index] = await Run(items[index], index, previous);
                    previous = results[index].Output;
                    if (results[index].ExitCode != 0) break;
                }
            }
        }
        finally
        {
            // Stop dispatch/waiting only. Foreground children are owned by the parent turn and were awaited above.
            batch.Cancel();
        }
        var completed = results.Where(result => result != null).ToArray();
        var failedTaskIds = completed.Where(result => result.ExitCode is not null and not 0).Select(result => result.TaskId).ToArray();
        var pending = completed.Count(result => result.ExitCode is null);
        var succeeded = completed.Count(result => result.ExitCode == 0);
        var outputLimit = 16000;
        ToolResult response;
        do
        {
            var bounded = completed.Select(result => result with {
                Output = ToolResult.Clip(result.Output, outputLimit),
                Error = result.Error == null ? null : ToolResult.Clip(result.Error, Math.Min(outputLimit, 2000)),
                OutputTruncated = result.Output.Length > outputLimit || (result.Error?.Length ?? 0) > Math.Min(outputLimit, 2000)
            }).ToArray();
            response = new(failedTaskIds.Length == 0, new { mode = parallel ? "parallel" : chain ? "chain" : "single", results = bounded,
                taskId = completed.Length == 1 ? completed[0].TaskId : null, failed = failedTaskIds, succeeded, pending },
                failedTaskIds.Length == 0 ? null : $"Delegated tasks failed, were cancelled or need parent input: {string.Join(", ", failedTaskIds)}. Succeeded: {succeeded}. results still contains the partial or successful outputs; do not rerun successful tasks."
                    + (chain ? " Subsequent chain steps were not executed." : ""),
                bounded.Any(result => result.OutputTruncated));
            outputLimit /= 2;
        } while (response.ToJson().Length > 60000 && outputLimit >= 128);
        return response;

        async Task<DelegationResult> Run(JsonElement item, int index, string? previous)
        {
            try
            {
                batch.Token.ThrowIfCancellationRequested();
                var agent = item.GetProperty("agent").GetString()!;
                var prompt = item.GetProperty("task").GetString()!;
                if (chain && previous != null) prompt = prompt.Replace("{previous}", previous, StringComparison.Ordinal);
                var request = single
                    ? new SubAgentRequest(agent, description.Length > 0 ? description : agent + " · " + Title(prompt), prompt, model.Length > 0 ? model : null, provider.Length > 0 ? provider : null, session)
                    : new SubAgentRequest(agent, agent + " · " + ToolResult.Clip(prompt, 60), prompt);
                var launched = await launch(request, batch.Token);
                started.Add(launched.TaskId);
                if (launched.Joined)
                    return new(agent, launched.TaskId, tasks.Get(launched.TaskId).SessionId, index + 1, null, "steered", "Steer delivered; the child keeps running and its result arrives as a completion notification.", null, true);
                var info = await tasks.WaitAsync(launched.TaskId, batch.Token);
                var output = info.State == BackgroundTaskState.Completed ? info.FinalOutput ?? info.Output : info.Output;
                return new(agent, launched.TaskId, info.SessionId, index + 1, SubAgentOutcome.ExitCode(info.State), info.State.ToString(), output, info.Error, SubAgentOutcome.IsPartial(info.State));
            }
            catch { batch.Cancel(); throw; }
        }
    }

    private ToolResult Cancel(JsonElement arguments)
    {
        ToolArguments.Validate(arguments, CancelSchema);
        var id = ToolArguments.Get<string>(arguments, "id", "");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Failure("A delegated task ID is required.");
        BackgroundTaskInfo info;
        try { info = tasks.Get(id); }
        catch (ArgumentException) { return ToolResult.Failure("Unknown delegated task ID."); }
        if (AgentExecutionScope.ConversationId is { } parent && info.ParentSessionId != parent)
            return ToolResult.Failure("Task is not owned by the current conversation.");
        tasks.Cancel(info.Id);
        return ToolResult.Success(new { cancelled = info.Id, status = nameof(BackgroundTaskState.Cancelling) });
    }

    private static string Title(string task) => ToolResult.Clip(task.Replace('\n', ' '), 60);

    private sealed record DelegationResult(string Agent, string? TaskId, Guid SessionId, int Step, int? ExitCode, string StopReason, string Output, string? Error, bool Partial)
    { public bool OutputTruncated { get; init; } }
}