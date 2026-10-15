using System.Text.Json;
using LanAgent.Core.Tools;
using LanAgent.Core.Data;

namespace LanAgent.Core.Services;

public enum BackgroundTaskState { Queued, Running, AwaitingApproval, AwaitingAnswer, Completed, Failed, Cancelled, Interrupted, Cancelling }
public sealed record BackgroundTaskInfo(string Id, string Title, Guid SessionId, string Kind, BackgroundTaskState State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Output = "", string? Error = null)
{
    /// <summary>Last completed assistant response; null for legacy records or unfinished jobs.</summary>
    public string? FinalOutput { get; init; }
    public string? OwnerId { get; init; }
    public Guid? ParentSessionId { get; init; }
    public bool NotifyParent { get; init; }
    public bool NotificationDelivered { get; init; }
    public string? CurrentStep { get; init; }
    public string[] Steps { get; init; } = [];
}
public sealed record BackgroundInteraction(ToolApprovalRequest? Approval, AgentQuestion? Question);
public sealed record BackgroundTaskActivity(long Version, IReadOnlyList<AgentEvent> Events);

/// <summary>Host-owned jobs, unlimited by default; positive concurrency opts into queuing. Restarted durable jobs are interrupted, never replayed automatically.</summary>
public sealed class BackgroundTasks : IAsyncDisposable
{
    private sealed class Running(BackgroundTaskInfo info)
    {
        public BackgroundTaskInfo Info = info;
        public CancellationTokenSource Cancellation = null!;
        public Task Task = Task.CompletedTask;
        public BackgroundInteraction? Interaction;
        public TaskCompletionSource<string?>? Response;
        public bool AllowsSteer;
        public readonly List<AgentEvent> Events = [];
        public long ActivityVersion;
    }
    private readonly Dictionary<string, Running> _jobs = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim? _slots;
    private readonly string? _path;
    private bool _disposed;
    private readonly bool _readOnly;
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private readonly FileStream? _ownerLease;
    public BackgroundTasks(string? dataDirectory = null, int concurrency = 0, bool readOnly = false)
    {
        if (concurrency < 0) throw new ArgumentException("任务并发必须为非负整数；0 表示不限并发。");
        _readOnly = readOnly; _slots = concurrency == 0 ? null : new(concurrency);
        if (dataDirectory != null) { Directory.CreateDirectory(dataDirectory); _path = Path.Combine(dataDirectory, "background-tasks.json"); }
        using var startup = _path == null || readOnly ? null : new StorageTransaction(_path);
        if (_path != null && !readOnly)
        {
            var owners = Path.Combine(dataDirectory!, "task-owners"); Directory.CreateDirectory(owners);
            _ownerLease = new FileStream(Path.Combine(owners, _ownerId + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        foreach (var item in ReadRecords())
        {
            if (!readOnly && OwnerAlive(item.OwnerId)) continue;
            var saved = !readOnly && IsActive(item.State) ? item with { State = BackgroundTaskState.Interrupted, Error = "应用退出中断任务；请显式重新发起。" } : item;
            if (!readOnly) saved = saved with { OwnerId = _ownerId };
            _jobs[saved.Id] = new(saved) { Cancellation = new() };
        }
        if (!readOnly) Save();
    }
    private bool OwnerAlive(string? owner)
    {
        if (_path == null || owner == null || owner.Length != 32 || owner.Any(c => !Uri.IsHexDigit(c))) return false;
        var path = Path.Combine(Path.GetDirectoryName(_path)!, "task-owners", owner + ".lock");
        if (!File.Exists(path)) return false;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    private BackgroundTaskInfo[] ReadRecords()
    {
        if (_path == null || !File.Exists(_path)) return [];
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("任务记录超过大小上限。");
        return JsonSerializer.Deserialize<BackgroundTaskInfo[]>(stream) ?? [];
    }
    public IReadOnlyList<BackgroundTaskInfo> List { get { lock (_sync) return (_readOnly ? ReadRecords() : _jobs.Values.Select(j => j.Info)).OrderByDescending(j => j.CreatedAt).ToArray(); } }
    public static bool IsActive(BackgroundTaskState state) => state is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.AwaitingAnswer or BackgroundTaskState.AwaitingApproval or BackgroundTaskState.Cancelling;
    public bool Active { get { lock (_sync) return _jobs.Values.Any(j => IsActive(j.Info.State)); } }
    private Running Find(string selector)
    { lock (_sync) return _jobs.Values.SingleOrDefault(j => j.Info.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("任务 ID 未唯一匹配。"); }
    public BackgroundTaskInfo Get(string id) { lock (_sync) return _readOnly ? List.Single(j => j.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)) : Find(id).Info; }
    public BackgroundInteraction? Interaction(string id) { lock (_sync) return Find(id).Interaction; }
    public BackgroundTaskActivity Activity(string id)
    {
        lock (_sync) { if (_readOnly) return new(0, []); var job = Find(id); return new(job.ActivityVersion, job.Events.ToArray()); }
    }
    public void AppendOutput(string id, string text)
    {
        lock (_sync) { var job = Find(id); job.Info = job.Info with { Output = ToolOutputWindow.Tail(job.Info.Output + text, 16000), UpdatedAt = DateTimeOffset.UtcNow }; }
    }
    /// <summary>Admission shares the completion lock, so an accepted steer cannot miss the final drain.</summary>
    public string? TryJoin(Guid session, Guid parent, Action<BackgroundTaskInfo> admit)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readOnly) throw new InvalidOperationException("任务快照只读。");
            var job = _jobs.Values.SingleOrDefault(j => j.Info.SessionId == session && IsActive(j.Info.State));
            if (job == null) return null;
            if (job.Info.ParentSessionId != parent) throw new ArgumentException("Task is not owned by the current parent session.");
            if (!job.AllowsSteer) throw new InvalidOperationException("This task host does not support steering.");
            if (job.Cancellation.IsCancellationRequested) throw new InvalidOperationException("Child cancellation is in progress; wait for completion before continuing.");
            admit(job.Info); return job.Info.Id;
        }
    }
    public string Start(string title, Guid session, string kind, Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> action, Guid? parentSession = null, Func<bool>? continueAfterStop = null, CancellationToken ownerToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readOnly) throw new InvalidOperationException("任务快照只读。");
            var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            var job = new Running(new(id, ToolResult.Clip(title, 120), session, kind, _slots == null ? BackgroundTaskState.Running : BackgroundTaskState.Queued, now, now) { ParentSessionId = parentSession, OwnerId = _ownerId }) { AllowsSteer = continueAfterStop != null, Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerToken) }; _jobs.Add(id, job); Save();
            job.Task = Task.Run(async () =>
            {
                var acquired = false;
                var responseText = "";
                var doneReceived = false;
                try
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    if (_slots != null) { await _slots.WaitAsync(job.Cancellation.Token); acquired = true; job.Cancellation.Token.ThrowIfCancellationRequested(); SetState(job, BackgroundTaskState.Running); }
                    while (true)
                    {
                        doneReceived = false; responseText = "";
                        var outcome = BackgroundTaskState.Failed;
                        await foreach (var evt in action(id, job.Cancellation.Token).WithCancellation(job.Cancellation.Token))
                        {
                            job.Cancellation.Token.ThrowIfCancellationRequested();
                            lock (_sync)
                            {
                                if (evt is AgentIteration) { responseText = ""; job.Events.Clear(); }
                                if (evt is AgentNotice { ResetResponse: true }) { responseText = ""; job.Info = job.Info with { Error = null, Output = "" }; job.Events.RemoveAll(e => e is TokenDelta or ThinkingDelta or AgentError); }
                                if (evt is TokenDelta text && job.Events.LastOrDefault() is TokenDelta previous)
                                    job.Events[^1] = new TokenDelta(ToolResult.Clip(previous.Text + text.Text, 32000));
                                else if (evt is ThinkingDelta thought && job.Events.LastOrDefault() is ThinkingDelta previousThought)
                                    job.Events[^1] = new ThinkingDelta(ToolResult.Clip(previousThought.Text + thought.Text, 32000));
                                else if (evt is not AgentContextUsage) job.Events.Add(evt);
                                if (job.Events.Count > 512) job.Events.RemoveRange(0, job.Events.Count - 512);
                                job.ActivityVersion++;
                                job.Info = job.Info with { UpdatedAt = DateTimeOffset.UtcNow };
                                if (evt is TokenDelta delta) responseText = ToolResult.Clip(responseText + delta.Text, 16000);
                                var step = evt switch
                                {
                                    AgentIteration iteration => $"第 {iteration.Current} 轮 · {iteration.ToolCalls} 次操作",
                                    AgentNotice notice => notice.Message,
                                    ToolCallStarted tool when tool.ToolName != "execute_code" => "正在" + OperationLabel(tool.ToolName),
                                    ToolCallCompleted tool when tool.ToolName != "execute_code" => (JsonSerializer.Deserialize<ToolResult>(tool.ResultJson, ToolResult.JsonOptions)?.Ok == true ? "已完成 · " : "失败 · ") + OperationLabel(tool.ToolName),
                                    _ => null
                                };
                                if (step != null) job.Info = job.Info with { CurrentStep = step, Steps = job.Info.Steps.Append(step).TakeLast(100).ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                                if (evt is TokenDelta token) job.Info = job.Info with { Output = ToolResult.Clip(job.Info.Output + token.Text, 16000), UpdatedAt = DateTimeOffset.UtcNow };
                                if (evt is AgentError error) job.Info = job.Info with { Error = ToolResult.Clip(error.Message) };
                                if (evt is AgentDone done)
                                {
                                    doneReceived = true;
                                    var state = done.FinishReason is "stop" or "STOP" ? BackgroundTaskState.Completed : done.FinishReason == "cancelled" ? BackgroundTaskState.Cancelled : BackgroundTaskState.Failed;
                                    outcome = state;
                                    job.Info = job.Info with { FinalOutput = state == BackgroundTaskState.Completed ? (string.IsNullOrEmpty(responseText) ? "Subagent completed without a text response." : responseText) : null, Error = state == BackgroundTaskState.Completed ? null : state == BackgroundTaskState.Failed ? job.Info.Error ?? done.FinishReason : job.Info.Error };
                                }
                            }
                        }
                        lock (_sync)
                        {
                            if (job.Cancellation.IsCancellationRequested) job.Info = job.Info with { State = BackgroundTaskState.Cancelled, FinalOutput = null };
                            else if (!doneReceived) job.Info = job.Info with { State = BackgroundTaskState.Failed, Error = job.Info.Error ?? "Agent stream ended without a completion event." };
                            else if (outcome == BackgroundTaskState.Completed && !job.Cancellation.IsCancellationRequested && continueAfterStop?.Invoke() == true) continue;
                            else job.Info = job.Info with { State = outcome, FinalOutput = outcome == BackgroundTaskState.Completed ? job.Info.FinalOutput : null };
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { lock (_sync) { job.Info = job.Info with { FinalOutput = null }; SetState(job, BackgroundTaskState.Cancelled); } }
                catch (Exception ex) { lock (_sync) job.Info = job.Info with { State = job.Cancellation.IsCancellationRequested ? BackgroundTaskState.Cancelled : BackgroundTaskState.Failed, FinalOutput = null, Error = ToolResult.Clip(ex.Message) }; }
                finally
                {
                    lock (_sync) { job.Response?.TrySetResult(null); job.Interaction = null; job.Response = null; job.Info = job.Info with { UpdatedAt = DateTimeOffset.UtcNow }; Save(); }
                    if (acquired) _slots!.Release();
                }
            });
            return id;
        }
    }
    private void SetState(Running job, BackgroundTaskState state) { lock (_sync) { job.Info = job.Info with { State = state, UpdatedAt = DateTimeOffset.UtcNow }; Save(); } }
    private static string OperationLabel(string name) => name switch
    {
        "file_read" => "读取文件", "file_write" => "写入文件", "file_edit" => "修改文件",
        "run_shell" => "运行命令", "list_directory" => "浏览目录", "shell_status" => "检查后台命令",
        _ => name
    };
    private async Task<string?> AskAsync(string id, BackgroundInteraction interaction, CancellationToken ct)
    {
        var job = Find(id); var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            job.Cancellation.Token.ThrowIfCancellationRequested();
            if (job.Response != null) throw new InvalidOperationException("任务已有待处理问题。");
            job.Response = response; job.Interaction = interaction; SetState(job, interaction.Approval != null ? BackgroundTaskState.AwaitingApproval : BackgroundTaskState.AwaitingAnswer);
        }
        try { return await response.Task.WaitAsync(ct); }
        finally { lock (_sync) { job.Interaction = null; job.Response = null; if (!job.Cancellation.IsCancellationRequested && job.Info.State != BackgroundTaskState.Cancelling) SetState(job, BackgroundTaskState.Running); } }
    }
    public async ValueTask<bool> ApproveAsync(string id, ToolApprovalRequest approval, CancellationToken ct) => await AskAsync(id, new BackgroundInteraction(approval, null), ct) == "allow";
    public Task<string?> AskAsync(string id, AgentQuestion question, CancellationToken ct)
    {
        if (Get(id).ParentSessionId != null)
            throw new InvalidOperationException("Delegated agents must return questions to the parent; they cannot wait for user answers.");
        return AskAsync(id, new BackgroundInteraction(null, question), ct);
    }
    public void Respond(string id, string? answer) { lock (_sync) { var job = Find(id); if (job.Response == null) throw new ArgumentException("任务没有等待授权或回答。"); job.Response.TrySetResult(answer); } }
    public void Cancel(string id)
    {
        var job = Find(id);
        lock (_sync)
        {
            if (!IsActive(job.Info.State)) return;
            job.Info = job.Info with { State = BackgroundTaskState.Cancelling, UpdatedAt = DateTimeOffset.UtcNow, FinalOutput = null };
            job.Interaction = null;
            Save();
        }
        job.Cancellation.Cancel();
        lock (_sync) job.Response?.TrySetResult(null);
    }
    public async Task CancelOwnedAsync(Guid parent, CancellationToken ct = default)
    {
        string[] ids;
        lock (_sync) ids = _jobs.Values.Where(j => j.Info.ParentSessionId == parent && IsActive(j.Info.State)).Select(j => j.Info.Id).ToArray();
        foreach (var id in ids) Cancel(id);
        await Task.WhenAll(ids.Select(id => WaitAsync(id, ct)));
    }
    /// <summary>A new interactive writer must wait until the old worker has actually stopped.</summary>
    public async Task StopSessionAsync(Guid session, CancellationToken ct = default)
    {
        string[] ids;
        lock (_sync) ids = _jobs.Values.Where(j => j.Info.SessionId == session && IsActive(j.Info.State)).Select(j => j.Info.Id).ToArray();
        foreach (var id in ids) Cancel(id);
        await Task.WhenAll(ids.Select(id => WaitAsync(id, ct)));
    }
    public async Task<BackgroundTaskInfo> WaitAsync(string id, CancellationToken ct = default) { var job = Find(id); await job.Task.WaitAsync(ct); return Get(id); }
    public void NotifyParentOnCompletion(string id)
    {
        lock (_sync) { var job = Find(id); job.Info = job.Info with { NotifyParent = true }; Save(); }
    }
    public IReadOnlyList<BackgroundTaskInfo> PendingNotifications(Guid parent) => List
        .Where(j => j.ParentSessionId == parent && j.NotifyParent && !j.NotificationDelivered &&
            j.State is BackgroundTaskState.Completed or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled or BackgroundTaskState.Interrupted)
        .OrderBy(j => j.UpdatedAt).ToArray();
    public void AcknowledgeNotification(string id)
    {
        lock (_sync) { var job = Find(id); job.Info = job.Info with { NotificationDelivered = true }; Save(); }
    }
    private void Save()
    {
        if (_path == null) return;
        lock (_sync)
        {
            using var transaction = new StorageTransaction(_path);
            var combined = ReadRecords().Where(j => !_jobs.ContainsKey(j.Id)).Concat(_jobs.Values.Select(j => j.Info)).ToArray();
            var active = combined.Where(j => IsActive(j.State));
            var history = combined.Where(j => !IsActive(j.State)).OrderByDescending(j => j.CreatedAt).Take(500);
            var items = active.Concat(history).OrderByDescending(j => j.CreatedAt).ToArray();
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })); File.Move(_path + ".tmp", _path, true);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Running[] jobs; lock (_sync) { if (_disposed) return; _disposed = true; jobs = _jobs.Values.ToArray(); }
        foreach (var job in jobs) job.Cancellation.Cancel(); await Task.WhenAll(jobs.Select(j => j.Task));
        foreach (var job in jobs) job.Cancellation.Dispose(); _slots?.Dispose(); _ownerLease?.Dispose();
    }
}
