using System.ComponentModel;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

/// <summary>Parent-only delegation. Children retain separate histories and host permissions.</summary>
public partial class SubAgentTools
{
    private readonly BackgroundTasks _tasks;
    private readonly Func<string, string?, string?, string> _start;
    private readonly Func<SubAgentRequest, string>? _delegate;
    private readonly Func<SubAgentRequest, CancellationToken, Task<string>>? _delegateAsync;

    public SubAgentTools(BackgroundTasks tasks, Func<string, string?, string?, string> start, Func<SubAgentRequest, string>? launch = null,
        Func<SubAgentRequest, CancellationToken, Task<string>>? launchAsync = null)
    { _tasks = tasks; _start = start; _delegate = launch; _delegateAsync = launchAsync; }

    [AgentTool("subagent"), Description("First inspect enough context to define a bounded task and separate edit scope. Delegate substantial independent work when the benefit exceeds startup and integration cost; keep small tasks and the critical dependent path with the parent. Use background children for independent tasks and collect their results; at most 6 children run concurrently across roles. Use ordinary parallel tool calls for short reads, searches and checks; handle small dependent work directly. Run a specialized child in its own conversation and microVM. general handles implementation and research; explore only searches and analyzes with host-enforced read-only permissions. Other configured roles and their descriptions are listed in the system instructions. Foreground is the default and returns the final answer; background returns immediately. Pass sessionID to continue a previous child or steer a running child of this conversation at its next model boundary. Steering joins the existing task; it does not interrupt an executing tool or resolve pending human approval/questions. Changing a running child's role updates its live host restrictions and next-step instructions; the parent's read-only mode remains binding. Explicit model/provider changes apply at its next model-step boundary. Include all relevant context in a new child's prompt. Set model/provider only if the user explicitly requested them. Resolve any pending approval/question in the task manager before waiting again.")]
    public async Task<ToolResult> DelegateAsync(
        [Description("Configured role name, including general and explore")] string agent,
        [Description("Short task label, 1–120 characters")] string description,
        [Description("Self-contained task, 1–12000 characters. Terminal hosts snapshot workspace @file references before admission; quoted paths support spaces. PNG/JPEG/GIF/WebP references are sent as image content, and text references include a bounded preview.")] string prompt,
        [Description("Optional previous child conversation ID")] string? sessionID = null,
        [Description("Return immediately instead of waiting for completion")] bool background = false,
        [Description("Optional user-requested model ID")] string? model = null,
        [Description("Optional user-requested configured provider")] string? provider = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentExecutionScope.IsSubAgent) return ToolResult.Failure("Subagent depth limit reached (1).");
        if (_delegate == null && _delegateAsync == null) return ToolResult.Failure("This host does not support specialized child sessions.");
        Guid? session = null;
        if (sessionID != null)
        {
            if (!Guid.TryParse(sessionID, out var parsed)) return ToolResult.Failure("Invalid subagent sessionID.");
            session = parsed;
        }
        if (model?.Length > 256 || provider?.Length > 256) return ToolResult.Failure("Model and provider selectors must not exceed 256 characters.");
        var request = new SubAgentRequest(agent, description, prompt, model, provider, session);
        var id = _delegateAsync != null ? await _delegateAsync(request, ct) : _delegate!(request);
        if (!background)
        {
            try
            {
                while (_tasks.Get(id).State is BackgroundTaskState.Queued or BackgroundTaskState.Running)
                    await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { _tasks.NotifyParentOnCompletion(id); throw; }
        }
        var info = _tasks.Get(id);
        if (background || info.State is BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval)
            _tasks.NotifyParentOnCompletion(id);
        return ToolResult.Success(new { taskId = id, sessionID = info.SessionId, status = info.State.ToString(), output = info.FinalOutput ?? info.Output, error = info.Error });
    }

    public SubAgentTools(BackgroundTasks tasks, Func<string, string> start)
        : this(tasks, (task, model, provider) =>
        {
            if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(provider))
                throw new InvalidOperationException("This host does not support child model overrides.");
            return start(task);
        }) { }

    [AgentTool("start_agent"), Description("Delegate a bounded, self-contained task to an independent child agent. Omit model and provider to use the configured child default (normally the current model). Returns {ok:true,data:{taskId:string},error:null,truncated:false} immediately; pass data.taskId as id to agent_status/wait_agent/cancel_agent. Child has its own conversation and microVM, shares the workspace, and must follow the same permission policy for its actions. Children cannot spawn agents. Use wait_agent or agent_status for results; show any waiting approval or question to the user.")]
    public Task<ToolResult> StartAsync(
        [Description("Self-contained delegated task, maximum 12000 characters")] string task,
        [Description("Optional child model ID, otherwise inherit the configured default")] string? model = null,
        [Description("Optional configured provider name or configuration ID")] string? provider = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentExecutionScope.IsSubAgent) return Task.FromResult(ToolResult.Failure("Child agents cannot delegate more agents."));
        if (string.IsNullOrWhiteSpace(task) || task.Length > 12000) return Task.FromResult(ToolResult.Failure("Task must be 1–12000 characters."));
        if (model?.Length > 256 || provider?.Length > 256) return Task.FromResult(ToolResult.Failure("Model and provider selectors must not exceed 256 characters."));
        var id = _start(task, model, provider);
        // Older embedding hosts may not provide durable task records.
        if (_tasks.List.Any(j => j.Id == id)) _tasks.NotifyParentOnCompletion(id);
        return Task.FromResult(ToolResult.Success(new { taskId = id }));
    }

    [AgentTool("agent_status"), Description("Read child agent state and bounded output. Poll sparingly. If approval or an answer is waiting, tell the user to resolve it using the task manager.")]
    public Task<ToolResult> StatusAsync([Description("Delegated task ID")] string id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Result(_tasks.Get(id))); }

    [AgentTool("wait_agent"), Description("Wait briefly for a child result without busy polling. Returns immediately when the child needs human approval or an answer. A still-running result means continue other work or wait again later.")]
    public async Task<ToolResult> WaitAsync([Description("Delegated task ID")] string id,
        [Description("Maximum wait in seconds, 0–30")] int timeoutSeconds = 10, CancellationToken ct = default)
    {
        if (timeoutSeconds is < 0 or > 30) return ToolResult.Failure("Wait timeout must be 0–30 seconds.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (_tasks.Get(id).State is BackgroundTaskState.Queued or BackgroundTaskState.Running)
                await Task.Delay(100, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        return Result(_tasks.Get(id));
    }

    [AgentTool("cancel_agent"), Description("Cancel a delegated child and its running sandbox.")]
    public Task<ToolResult> CancelAsync([Description("Delegated task ID")] string id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); _tasks.Cancel(id); return Task.FromResult(ToolResult.Success(new { cancelled = id })); }

    private static ToolResult Result(BackgroundTaskInfo info)
    {
        var output = info.State == BackgroundTaskState.Completed ? info.FinalOutput ?? info.Output : info.Output;
        return ToolResult.Success(info with { Output = ToolResult.Clip(output) }, output.Length > ToolResult.MaxOutputCharacters);
    }
}
