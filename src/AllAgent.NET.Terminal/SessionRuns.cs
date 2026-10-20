using System.Collections.Concurrent;
using System.Threading.Channels;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;

namespace AllAgent.NET.Terminal;

internal enum SessionRunState { Running, AwaitingApproval, AwaitingAnswer, AwaitingPlan, Cancelling, Done, Failed }

internal sealed record PendingApproval(ApprovalPreview Preview, TaskCompletionSource<bool> Completion, CancellationToken Token);
internal sealed record PendingQuestion(AgentQuestion Question, TaskCompletionSource<string?> Completion, CancellationToken Token);
internal sealed record PendingPlan(PlanPreview Preview, TaskCompletionSource<PlanDecision> Completion, CancellationToken Token);

/// <summary>
/// One main-session turn with a life of its own: the pump that drains the engine lives here, not in
/// the view, so leaving the session (another conversation, `/new`, another workspace) keeps the work
/// going. Every event is journaled, which is what lets a later view rebuild the live tail.
/// </summary>
internal sealed class SessionRun(Guid conversationId, string title, string streamId, BridgeStreamJournal journal, CancellationTokenSource cancellation)
{
    public Guid ConversationId { get; } = conversationId;
    public string Title { get; } = title;
    public string StreamId { get; } = streamId;
    public BridgeStreamJournal Journal { get; } = journal;
    public CancellationTokenSource Cancellation { get; } = cancellation;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public volatile SessionRunState State = SessionRunState.Running;
    public string? FinishReason { get; set; }
    public string? Failure { get; set; }

    private readonly ConcurrentQueue<PendingApproval> _approvals = new();
    private readonly ConcurrentQueue<PendingQuestion> _questions = new();
    private readonly ConcurrentQueue<PendingPlan> _plans = new();
    private Channel<(long Sequence, AgentEvent Event)>? _channel;
    private volatile bool _released;

    public bool Attached => Volatile.Read(ref _channel) != null;
    public bool IsFinished => State is SessionRunState.Done or SessionRunState.Failed;
    public bool Waiting => State is SessionRunState.AwaitingApproval or SessionRunState.AwaitingAnswer or SessionRunState.AwaitingPlan;
    public int PendingPrompts => _approvals.Count + _questions.Count + _plans.Count;

    /// <summary>Starts forwarding live events to a viewer and returns the reader to consume them from.
    /// Events appended before this call stay in the journal, so the viewer replays it and skips the overlap.</summary>
    public ChannelReader<(long Sequence, AgentEvent Event)> Attach()
    {
        var channel = Channel.CreateUnbounded<(long, AgentEvent)>(new UnboundedChannelOptions { SingleReader = true });
        var previous = Interlocked.Exchange(ref _channel, channel);
        previous?.Writer.TryComplete();
        return channel.Reader;
    }

    public void Detach() => Interlocked.Exchange(ref _channel, null)?.Writer.TryComplete();

    public void Publish(long sequence, AgentEvent evt) => Volatile.Read(ref _channel)?.Writer.TryWrite((sequence, evt));

    /// <summary>A prompt raised while nobody is watching waits here instead of being answered for the user.</summary>
    public void EnqueueApproval(PendingApproval approval)
    {
        if (_released) { approval.Completion.TrySetResult(false); return; }
        _approvals.Enqueue(approval);
        if (_released) ReleasePrompts();
    }
    public void EnqueueQuestion(PendingQuestion question)
    {
        if (_released) { question.Completion.TrySetResult(null); return; }
        _questions.Enqueue(question);
        if (_released) ReleasePrompts();
    }
    public void EnqueuePlan(PendingPlan plan)
    {
        if (_released) { plan.Completion.TrySetResult(new PlanDecision(false, "Plan approval was released because the run ended; stay in plan mode.")); return; }
        _plans.Enqueue(plan);
        if (_released) ReleasePrompts();
    }
    public bool TryDequeueApproval(out PendingApproval? approval) => _approvals.TryDequeue(out approval);
    public bool TryDequeueQuestion(out PendingQuestion? question) => _questions.TryDequeue(out question);
    public bool TryDequeuePlan(out PendingPlan? plan) => _plans.TryDequeue(out plan);

    /// <summary>Answers everything still waiting — a run that ends or is stopped must not leave a prompt hanging.</summary>
    public void ReleasePrompts()
    {
        _released = true;
        while (_approvals.TryDequeue(out var approval)) approval.Completion.TrySetResult(false);
        while (_questions.TryDequeue(out var question)) question.Completion.TrySetResult(null);
        while (_plans.TryDequeue(out var plan)) plan.Completion.TrySetResult(new PlanDecision(false, "Plan approval was released because the run ended; stay in plan mode."));
    }
}

/// <summary>Every main-session run alive in this workspace. One run per conversation, any number at once.</summary>
internal sealed class SessionRuns
{
    private readonly ConcurrentDictionary<Guid, SessionRun> _runs = new();
    private readonly ConcurrentQueue<SessionRun> _finished = new();
    private int _revision;

    /// <summary>Bumped on every state change; the full-screen renderer keys its frames on it.</summary>
    public int Revision => Volatile.Read(ref _revision);
    public bool Any => !_runs.IsEmpty;
    public int Count => _runs.Count;
    public IReadOnlyList<SessionRun> Active => _runs.Values.OrderBy(run => run.StartedAt).ToArray();

    public bool IsRunning(Guid id) => _runs.ContainsKey(id);
    public SessionRun? Find(Guid id) => _runs.TryGetValue(id, out var run) ? run : null;
    public SessionRun? Find(Guid? id) => id is { } value ? Find(value) : null;

    /// <summary>Short status suffix for a prompt or list row, empty when the session is idle.</summary>
    public string Label(Guid id) => Find(id)?.State switch
    {
        null => "",
        SessionRunState.AwaitingApproval => L.Pick(" · 需要授权", " · approval needed"),
        SessionRunState.AwaitingAnswer => L.Pick(" · 需要回答", " · answer needed"),
        SessionRunState.AwaitingPlan => L.Pick(" · 等待计划审批", " · plan approval pending"),
        SessionRunState.Cancelling => L.Pick(" · 正在停止", " · stopping"),
        _ => L.Pick(" · 运行中", " · running"),
    };

    /// <summary>Runs that finished in the background and have not been reported yet.</summary>
    public IReadOnlyList<SessionRun> TakeFinished()
    {
        var finished = new List<SessionRun>();
        while (_finished.TryDequeue(out var run)) finished.Add(run);
        return finished;
    }

    public SessionRun Start(DataStore store, WorkspaceCheckpoints checkpoints, Guid conversationId, AgentEngine engine, string prompt,
        ApiConfig config, bool internalMessage = false, IReadOnlyList<ImageAttachment>? images = null, string? inputText = null, bool fromInbox = false)
    {
        var history = store.GetVisibleMessages(conversationId).Where(message => !message.IsInternal).ToList();
        // The turn's own user message is drawn by the input line, never by an event, so a view that
        // rebuilds from this journal (attach or re-attach) has to find it here.
        if (!internalMessage && !fromInbox)
            history.Add(new Message { ConversationId = conversationId, Role = "user", Content = prompt, InputText = inputText ?? prompt, Images = images?.ToList() ?? [] });
        var journal = new BridgeStreamJournal(history, inputText ?? prompt);
        var run = new SessionRun(conversationId, store.GetConversation(conversationId)?.Title ?? L.Pick("会话", "Conversation"), Guid.NewGuid().ToString("N"), journal, new CancellationTokenSource());
        if (!_runs.TryAdd(conversationId, run)) throw new InvalidOperationException(L.Pick("该会话已有任务正在运行；输入文字可作为指导发送，或先停止它。", "This conversation already has a running task; send text as guidance or stop it first."));
        checkpoints.Begin(conversationId, prompt, store.GetVisibleMessages(conversationId));
        Bump();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in engine.RunAsync(conversationId, prompt, config, run.Cancellation.Token,
                    internalMessage: internalMessage, images: images, fromInbox: fromInbox, inputText: inputText))
                {
                    var sequence = journal.Append(evt);
                    switch (evt)
                    {
                        case AgentDone done: run.FinishReason = done.FinishReason; break;
                        case AgentError error: run.Failure = error.Message; run.State = SessionRunState.Failed; break;
                    }
                    run.Publish(sequence, evt);
                }
                if (run.State == SessionRunState.Running) run.State = SessionRunState.Done;
            }
            catch (OperationCanceledException)
            {
                run.State = SessionRunState.Done;
                if (run.FinishReason == null) run.FinishReason = "cancelled";
            }
            catch (Exception ex)
            {
                run.Failure = ex.Message; run.State = SessionRunState.Failed;
            }
            finally
            {
                try { checkpoints.End(conversationId, store.GetVisibleMessages(conversationId)); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { /* a failed snapshot must not hide the run result */ }
                var watched = run.Attached;
                _runs.TryRemove(conversationId, out _);
                run.Detach(); run.ReleasePrompts();
                run.Cancellation.Dispose();
                run.Completion.TrySetResult();
                // A run nobody was watching is announced instead of ending silently.
                if (!watched) _finished.Enqueue(run);
                Bump();
            }
        });
        return run;
    }

    /// <summary>Stops a run by conversation — used by the session list and by Esc on a live view.</summary>
    public void RequestStop(SessionRun run)
    {
        if (run.State is SessionRunState.Done or SessionRunState.Failed) return;
        run.State = SessionRunState.Cancelling;
        Bump();
        try { run.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<bool> StopAsync(Guid conversationId, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(conversationId, out var run)) return false;
        RequestStop(run);
        await run.Completion.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>Stops everything and waits for the pumps to finish — workspace teardown must not leave writers behind.</summary>
    public async Task StopAllAsync(TimeSpan timeout)
    {
        foreach (var run in _runs.Values.ToArray()) RequestStop(run);
        try { await Task.WhenAll(_runs.Values.Select(run => run.Completion.Task)).WaitAsync(timeout); }
        catch (TimeoutException) { /* a stuck run must not block shutdown */ }
    }

    public void Bump()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    /// <summary>Raised whenever runs start, change state or finish.</summary>
    public event Action? Changed;
}
