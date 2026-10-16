using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

/// <summary>Host-owned delegation with isolated single, parallel and sequential tasks.</summary>
public sealed class AgentDelegationTools(BackgroundTasks tasks, Func<SubAgentRequest, CancellationToken, Task<string>> launch) : IAgentToolSet
{
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"properties":{
          "agent":{"type":"string","description":"Configured agent name for single mode"},
          "task":{"type":"string","description":"Self-contained delegated task for single mode"},
          "tasks":{"type":"array","maxItems":8,"description":"Parallel tasks; at most 8, concurrency 6","items":{"type":"object","additionalProperties":false,"properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}},
          "chain":{"type":"array","description":"Sequential tasks; {previous} in task inserts the preceding result","items":{"type":"object","additionalProperties":false,"properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}}
        }}
        """);
    public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = [new("subagent",
        "After inspecting enough context to define bounded tasks and separate edit scopes, delegate substantial independent work when the benefit exceeds startup and integration cost. Keep small tasks and the critical dependent path with the parent. Batch independent module investigations, implementations with separate edit scopes, or research tasks together. Use ordinary parallel tool calls for short operations; handle small dependent work directly. Across roles at most 6 children run concurrently. Provide exactly one mode: agent+task, tasks (parallel, max 8/concurrency 6), or chain (sequential with {previous}). Inherits dispatch model and permissions. Returns final results, errors and exit codes. Children cannot ask the user or delegate. Cancelling the parent stops dispatch and waiting; already admitted children continue in the background and notify the parent when finished. Explicit task cancellation stops a child. Missing information is returned to the parent.", Schema, ToolRisk.Read)];

    public async ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (name != "subagent") return ToolResult.Failure("Unknown tool: " + name);
        if (AgentExecutionScope.IsSubAgent) return ToolResult.Failure("Delegated agents cannot delegate further tasks.");
        ToolArguments.Validate(arguments, Schema);
        var single = arguments.TryGetProperty("agent", out _) || arguments.TryGetProperty("task", out _);
        var parallel = arguments.TryGetProperty("tasks", out var parallelItems);
        var chain = arguments.TryGetProperty("chain", out var chainItems);
        if ((single ? 1 : 0) + (parallel ? 1 : 0) + (chain ? 1 : 0) != 1)
            return ToolResult.Failure("Provide exactly one mode: agent + task, tasks, or chain.");
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
            // Stop dispatch/waiting only. Admitted jobs have an independent lifetime.
            batch.Cancel();
            foreach (var id in started)
                if (ct.IsCancellationRequested || results.Any(result => result == null) || BackgroundTasks.IsActive(tasks.Get(id).State))
                    tasks.NotifyParentOnCompletion(id);
        }
        var completed = results.Where(result => result != null).ToArray();
        var failed = completed.Any(result => result.ExitCode != 0);
        var failedTaskIds = completed.Where(result => result.ExitCode != 0).Select(result => result.TaskId).ToArray();
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
            response = new(!failed, new { mode = parallel ? "parallel" : chain ? "chain" : "single", results = bounded,
                taskId = completed.Length == 1 ? completed[0].TaskId : null, failed = failedTaskIds, succeeded },
                failed ? $"Delegated tasks failed, were cancelled or need parent input: {string.Join(", ", failedTaskIds)}. Succeeded: {succeeded}. results still contains successful outputs; do not rerun successful tasks." +
                    (chain ? " Subsequent chain steps were not executed." : "") : null,
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
            var id = await launch(new(agent, agent + " · " + ToolResult.Clip(prompt, 60), prompt), batch.Token);
            started.Add(id);
            var info = await tasks.WaitAsync(id, batch.Token);
            return new(agent, id, info.SessionId, index + 1, info.State == BackgroundTaskState.Completed ? 0 : info.State == BackgroundTaskState.Cancelled ? 130 : 1,
                info.State.ToString(), info.FinalOutput ?? info.Output, info.Error);
            }
            catch { batch.Cancel(); throw; }
        }
    }
    private sealed record DelegationResult(string Agent, string TaskId, Guid SessionId, int Step, int ExitCode, string StopReason, string Output, string? Error)
    { public bool OutputTruncated { get; init; } }
}
