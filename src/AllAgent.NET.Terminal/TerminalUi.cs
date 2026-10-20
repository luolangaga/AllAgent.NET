using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

internal sealed class ToolsApproval(TerminalUi ui)
{
    public bool PlanMode { get; set; }
    /// <summary>Simple mode narrows the model-facing tool surface to PowerShell, file_read and
    /// file_edit. The approval stance below keeps applying unchanged.</summary>
    public bool SimpleMode { get; set; }
    /// <summary>Host permission mode. Restricted auto-answers workspace-contained operations in the
    /// runtime, so only boundary crossings, untrusted programs and external services reach this prompt.</summary>
    public PermissionMode Permission { get; set; } = PermissionMode.Restricted;
    public bool FullAccess => Permission == PermissionMode.Full;
    public string ModeKey => Permission switch { PermissionMode.Full => "full", PermissionMode.Restricted => "restricted", _ => "ask" };
    public string ModeLabel => PlanMode ? L.Pick("计划模式", "Plan mode") : SimpleMode ? L.Pick("简单模式", "Simple mode") : Permission switch
    {
        PermissionMode.Full => L.Pick("完全访问", "Full access"),
        PermissionMode.Restricted => L.Pick("限制模式", "Restricted"),
        _ => L.Pick("逐次授权", "Ask each time")
    };
    public WorkspaceCheckpoints? Checkpoints { get; set; }
    public async ValueTask<bool> ApproveAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        if (PlanMode && request.Risk != ToolRisk.Read) return false;
        if (PlanMode && request.Name == "external_directory" && request.Arguments.GetProperty("operation").GetString() is not ("file_read" or "file_search" or "everything_search" or "list_directory")) return false;
        if (FullAccess && !request.RequiresExplicitApproval) { Checkpoints?.MarkExternalAction(); return true; }
        if (request.Preapproved && !request.RequiresExplicitApproval) return true;
        if (!ui.Interactive || ui.Json) return false;
        var allowed = await ui.RequestApprovalAsync(request, ct);
        if (allowed && (request.Name.StartsWith("mcp_", StringComparison.Ordinal) || request.Name.StartsWith("computer_", StringComparison.Ordinal))) Checkpoints?.MarkExternalAction();
        return allowed;
    }
}

internal sealed class TerminalUi(bool json, bool verbose)
{
    public bool Verbose { get; set; } = verbose;
    public bool ShowThinking { get; set; } = true;
    public bool Json { get; } = json;
    public bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public string Workspace { get; set; } = Environment.CurrentDirectory;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public InputInbox? Inbox { get; set; }
    public int FooterRevision { get; set; }
    public string Draft { get; set; } = "";
    public string LastReason { get; private set; } = "stop";
    public long TurnInputTokens { get; private set; }
    public long TurnOutputTokens { get; private set; }
    private bool _hasTurnUsage;
    public string TurnTokenLabel => !_hasTurnUsage
        ? L.Pick("本次 Token：等待模型上报", "Tokens this turn: waiting for the model")
        : L.Pick($"本次 Token {TurnInputTokens + TurnOutputTokens:N0} (入 {TurnInputTokens:N0} / 出 {TurnOutputTokens:N0})",
            $"Tokens this turn {TurnInputTokens + TurnOutputTokens:N0} (in {TurnInputTokens:N0} / out {TurnOutputTokens:N0})");
    public int ContextUsedTokens { get; private set; }
    public int ContextCapacityTokens { get; private set; } = 100000;
    public bool ContextEstimated { get; private set; } = true;
    private string? _contextModel;
    public string ThinkingEffortLabel { get; private set; } = L.Pick("思考强度 未设置", "Reasoning unset");
    public void SetThinkingEffort(AllAgent.NET.Core.Models.ApiConfig? config) => ThinkingEffortLabel = config == null
        ? L.Pick("思考强度 未设置", "Reasoning unset")
        : L.Pick("思考强度 ", "Reasoning ") + AllAgent.NET.Core.Services.AI.ReasoningSettings.Label(config);
    public string ContextLabel => L.Pick("上下文 ", "Context ") + (ContextEstimated ? "≈" : "") + AllAgent.NET.Core.Services.AI.ModelContext.Format(ContextUsedTokens) + " / " + AllAgent.NET.Core.Services.AI.ModelContext.Format(ContextCapacityTokens);
    public void SetModelContext(AllAgent.NET.Core.Models.ApiConfig config)
    {
        SetThinkingEffort(config);
        var key = config.Id + ":" + config.DefaultModel;
        if (_contextModel != key) { ContextUsedTokens = 0; ContextEstimated = true; _contextModel = key; }
        ContextCapacityTokens = AllAgent.NET.Core.Services.AI.ModelContext.Capacity(config);
    }
    public void RestoreContextUsage(AllAgent.NET.Core.Models.Conversation? conversation)
    {
        if (conversation?.ContextUsedTokens is { } used && conversation.ContextUsageModel == conversation.ModelId)
        { ContextUsedTokens = used; ContextEstimated = conversation.ContextUsageEstimated; }
        else { ContextUsedTokens = (conversation?.Messages.Sum(message => message.Content.Length + (message.ToolCallsJson?.Length ?? 0)) ?? 0) / 4; ContextEstimated = true; }
    }
    private readonly ConcurrentQueue<PendingApproval> _approvals = new();
    private readonly ConcurrentQueue<PendingQuestion> _questions = new();
    private readonly ConcurrentQueue<PendingPlan> _plans = new();
    private readonly StringBuilder _answer = new();
    private readonly StringBuilder _thought = new();
    private readonly StringBuilder _taskThought = new();
    private int _thinkingAttemptStart;
    private readonly Stopwatch _elapsed = new();
    private enum Stage { Thinking, Answering, Running, ExecutingTool, WaitingContinue, WaitingPlanRevision, HandlingChildResults, CorrectingProtocol, WaitingRetry, AutoContinue, ReceivingInput }
    private Stage _stage = Stage.Thinking;
    private string StageLabel => _stage switch
    {
        Stage.Thinking => L.Pick("思考中", "Thinking"),
        Stage.Answering => L.Pick("回答中", "Answering"),
        Stage.Running => L.Pick("执行中", "Running"),
        Stage.ExecutingTool => L.Pick("执行工具", "Executing tools"),
        Stage.WaitingContinue => L.Pick("等待模型继续", "Waiting for the model"),
        Stage.WaitingPlanRevision => L.Pick("等待模型修订计划", "Waiting for a revised plan"),
        Stage.HandlingChildResults => L.Pick("处理子代理结果", "Processing child results"),
        Stage.CorrectingProtocol => L.Pick("纠正调用协议", "Correcting tool protocol"),
        Stage.WaitingRetry => L.Pick("等待网络重试", "Waiting to retry"),
        Stage.AutoContinue => L.Pick("自动继续", "Continuing automatically"),
        Stage.ReceivingInput => L.Pick("收到新输入", "New input received"),
        _ => ""
    };
    private bool _processingChildResults;
    private int _toolCalls;
    private volatile bool _live;
    public bool AgentIsLive => _live;
    public Func<string, CancellationToken, Task>? RunningCommand { get; set; }
    /// <summary>Main-session runs of this workspace. Approvals, questions and stop requests of a
    /// background run belong to that run, so leaving the view never answers for the person.</summary>
    public SessionRuns? Runs { get; set; }
    /// <summary>Set by a running command that moves the view elsewhere (/new, /session, /workspace):
    /// the view detaches while the run keeps working.</summary>
    public bool DetachRequested { get; set; }
    private SessionRun? _attachedRun;
    internal static IReadOnlyList<CommandHint> RunningHints => CommandCatalog.Builtins.Where(hint =>
        new[] { "/new", "/session", "/tasks", "/queue", "/steer", "/todo", "/help", "/status", "/thinking", "/details" }.Contains(hint.Text.Split(' ')[0])).ToArray();
    private PendingApproval? _pending;
    private PendingQuestion? _question;
    private PendingPlan? _plan;
    private int _questionChoice;
    private int _questionScroll;
    private InputState _questionText = new();
    private int _approvalChoice = 0, _approvalScroll;
    private int _planChoice = 1, _planScroll;
    private bool _planExpanded;
    private readonly InputState _planComment = new();
    private bool _planCommentActive;
    private bool _expanded;
    private InputState _queued = new();
    private bool _plainStreaming;
    private readonly Dictionary<string, CodeExecutionBlock> _codeBlocks = [];
    private readonly Dictionary<string, CodeExecutionBlock> _hostBlocks = [];

    public async Task<string?> RequestQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        if (!Interactive || Json) return null;
        // A question from a background run waits on that run; it is shown when the session comes back.
        if (Runs?.Find(AgentExecutionScope.ConversationId) is { } run)
        {
            var waiting = new PendingQuestion(question, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
            run.EnqueueQuestion(waiting);
            if (!run.Attached) run.State = SessionRunState.AwaitingAnswer;
            Runs.Bump();
            try { return await waiting.Completion.Task.WaitAsync(ct); }
            finally { if (!waiting.Completion.Task.IsCompleted) waiting.Completion.TrySetResult(null); }
        }
        if (!_live)
        {
            if (FullScreenTerminal.Current is { } screen) return await screen.QuestionAsync(question, ct);
            if (question.Choices.Count > 0) return await TerminalQuestionPrompt.AskAsync(AnsiConsole.Console, new TerminalKeyReader(), question, ct);
            TerminalOutput.Write(new Panel(new Text(question.Question)).Header(L.Pick("需要你的回答", "Your answer is needed")));
            return await ManagementUi.Text(L.Pick("请输入回答", "Enter your answer"));
        }
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions.Enqueue(new(question, completion, ct));
        try { return await completion.Task.WaitAsync(ct); }
        finally { if (!completion.Task.IsCompleted) completion.TrySetResult(null); }
    }

    public void Header(string workspace, string model, Guid session)
    {
        Workspace = workspace;
        if (Inbox != null) { Inbox.Session = session; if (Inbox.SessionIsReverted) Inbox.AutomaticDeliveryPaused = true; }
        if (FullScreenTerminal.Current is { } screen) { screen.Session(session, model); return; }
        TerminalOutput.WriteLine();
        TerminalOutput.Write(new Markup("[bold #d97757]▐▛███▜▌[/]  [bold]AllAgent[/]  [dim]CodeAct · Hyperlight[/]\n"));
        TerminalOutput.Write(new Text($"  {model} · {L.Pick("会话", "session")} {session.ToString()[..8]}\n  {workspace}\n", new Style(Color.Grey)));
        TerminalOutput.MarkupLine(L.Pick(
            "[dim]  / 命令 · /providers 模型 · /skills 技能 · /mcp 扩展 · Esc 停止 · /exit 退出[/]",
            "[dim]  / commands · /providers models · /skills skills · /mcp extensions · Esc stop · /exit quit[/]"));
    }
    public async ValueTask<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var preview = ApprovalPreview.Create(request, Workspace);
        // An approval raised by a background run waits on that run and is asked when its session is
        // back on screen — it is never answered (or silently denied) on the person's behalf.
        if (Runs?.Find(AgentExecutionScope.ConversationId) is { } run)
        {
            var waiting = new PendingApproval(preview, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
            run.EnqueueApproval(waiting);
            if (!run.Attached) run.State = SessionRunState.AwaitingApproval;
            Runs.Bump();
            try { return await waiting.Completion.Task.WaitAsync(ct); }
            finally { if (!waiting.Completion.Task.IsCompleted) waiting.Completion.TrySetResult(false); }
        }
        if (_live)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _approvals.Enqueue(new(preview, completion, ct));
            try { return await completion.Task.WaitAsync(ct); }
            finally { if (!completion.Task.IsCompleted) completion.TrySetResult(false); }
        }
        await Gate.WaitAsync(ct);
        try
        {
            if (FullScreenTerminal.Current is { } screen) return await screen.ApproveAsync(preview, ct);
            TerminalOutput.WriteLine(); TerminalOutput.Write(preview.Render());
            return await AnsiConsole.PromptAsync(new ConfirmationPrompt(L.Pick("允许这一次操作？", "Allow this operation?")) { DefaultValue = true }, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task<PlanDecision> RequestPlanApprovalAsync(PlanProposal plan, CancellationToken ct)
    {
        if (!Interactive || Json) return new PlanDecision(false, "Plan approval requires an interactive terminal; stay in plan mode.");
        var preview = new PlanPreview(plan);
        // A plan raised by a background run waits on that run and is asked when its session is
        // back on screen — never answered (or silently denied) on the person's behalf.
        if (Runs?.Find(AgentExecutionScope.ConversationId) is { } run)
        {
            var waiting = new PendingPlan(preview, new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
            run.EnqueuePlan(waiting);
            if (!run.Attached) run.State = SessionRunState.AwaitingPlan;
            Runs.Bump();
            try { return await waiting.Completion.Task.WaitAsync(ct); }
            finally { if (!waiting.Completion.Task.IsCompleted) waiting.Completion.TrySetResult(new PlanDecision(false)); }
        }
        if (_live)
        {
            var completion = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.Enqueue(new(preview, completion, ct));
            try { return await completion.Task.WaitAsync(ct); }
            finally { if (!completion.Task.IsCompleted) completion.TrySetResult(new PlanDecision(false)); }
        }
        await Gate.WaitAsync(ct);
        try
        {
            if (FullScreenTerminal.Current is { } screen) return await screen.ApprovePlanAsync(preview, ct);
            TerminalOutput.WriteLine(); TerminalOutput.Write(TerminalMarkdown.Render(plan.Markdown));
            var approved = await AnsiConsole.PromptAsync(new ConfirmationPrompt(L.Pick("批准计划并进入完全访问？", "Approve the plan and switch to full access?")) { DefaultValue = false }, ct);
            return new PlanDecision(approved);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Runs an event sequence with a live view but without a session run (/compact and friends).</summary>
    public Task RunAgentAsync(IAsyncEnumerable<AgentEvent> events, CancellationToken ct) => ViewAsync(events, null, null, 0, null, ct);

    /// <summary>Attaches the view to a session run. Leaving the view — a session switch, `/new`, a
    /// workspace change — never stops the run; its events stay journaled, so switching back rebuilds
    /// the live tail exactly where it was.</summary>
    public async Task AttachRunAsync(SessionRun run, CancellationToken ct)
    {
        var feed = run.Attach();
        var replay = run.Journal.Events().ToArray();
        var through = replay.Length == 0 ? 0 : replay[^1].Sequence;
        await ViewAsync(null, run, feed, through, replay, ct);
    }

    private async Task ViewAsync(IAsyncEnumerable<AgentEvent>? events, SessionRun? run,
        ChannelReader<(long Sequence, AgentEvent Event)>? feed, long replayedThrough,
        IReadOnlyList<(long Sequence, AgentEvent Event, DateTime Timestamp)>? replay, CancellationToken ct)
    {
        TurnInputTokens = TurnOutputTokens = 0; _hasTurnUsage = false;
        if (Json || !Interactive || !AnsiConsole.Profile.Capabilities.Ansi)
        {
            if (replay != null) foreach (var (_, replayed, _) in replay) await RenderAsync(replayed, CancellationToken.None);
            if (events != null) { await foreach (var evt in events) await RenderAsync(evt, CancellationToken.None); return; }
            if (feed == null) return;
            // No full-screen key hook here: Ctrl+C stops the run through the console event instead.
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; if (run != null) Runs?.RequestStop(run); };
            Console.CancelKeyPress += handler;
            try
            {
                while (await feed.WaitToReadAsync(ct))
                    if (feed.TryRead(out var item) && item.Sequence > replayedThrough) await RenderAsync(item.Event, CancellationToken.None);
            }
            catch (OperationCanceledException) { if (run != null) Runs?.RequestStop(run); throw; }
            finally { Console.CancelKeyPress -= handler; }
            return;
        }
        using var inputMode = new TextInputMode();
        _answer.Clear(); _thought.Clear(); _taskThought.Clear(); _thinkingAttemptStart = 0; _stage = Stage.Thinking; _toolCalls = 0; _elapsed.Restart(); _live = true;
        _processingChildResults = false;
        _attachedRun = run;
        _queued = new InputState(); _queued.Set(Draft); Draft = "";
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? runningCommand = null;
        try
        {
            // A session that is already working rebuilds from the run's own history: the persisted
            // conversation may already contain iterations that this journal still replays.
            if (run != null && FullScreenTerminal.Current is { } target && target.SessionId == run.ConversationId)
                target.Session(run.ConversationId, target.Model, run.Journal.History, reload: true);
            if (replay != null) foreach (var (_, replayed, _) in replay) await RenderAsync(replayed, CancellationToken.None);
            SpectreScreenDisplay? inlineDisplay = null;
            async Task PumpAsync(Action<IRenderable> refresh)
            {
                var reader = FullScreenTerminal.Current?.Keys ?? new TerminalKeyReader();
                var enumerator = events?.GetAsyncEnumerator(cancelled.Token);
                var next = NextAsync();
                long lastRefresh = 0;
                void Stop()
                {
                    // Stopping a session run is a decision about that run, not about this view.
                    if (run != null) Runs?.RequestStop(run);
                    else cancelled.Cancel();
                }
                async Task<(bool Has, AgentEvent? Event)> NextAsync()
                {
                    if (feed != null)
                    {
                        while (await feed.WaitToReadAsync(cancelled.Token))
                            if (feed.TryRead(out var item) && item.Sequence > replayedThrough) return (true, item.Event);
                        return (false, null);
                    }
                    if (enumerator != null) return await enumerator.MoveNextAsync() ? (true, enumerator.Current) : (false, null);
                    return (false, null);
                }
                while (true)
                {
                    if (DetachRequested) { DetachRequested = false; FlushAnswer(); return; }
                    if (runningCommand?.IsCompleted == true)
                    {
                        try { await runningCommand; }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Error(ex.Message); }
                        runningCommand = null;
                    }
                    if (_pending == null && _question == null && _plan == null && TryDequeueApproval(run, out var approval)) { _pending = approval; _approvalChoice = 0; _approvalScroll = 0; _expanded = false; FlushAnswer(); }
                    if (_pending?.Token.IsCancellationRequested == true) Decide(false);
                    if (_question == null && _plan == null && _pending == null && TryDequeueQuestion(run, out var question)) { _question = question; _questionChoice = _questionScroll = 0; _questionText = new(); FlushAnswer(); }
                    if (_question?.Token.IsCancellationRequested == true) AnswerQuestion(null);
                    if (_plan == null && _pending == null && _question == null && TryDequeuePlan(run, out var plan)) { _plan = plan; _planChoice = 1; _planScroll = 0; _planExpanded = false; _planCommentActive = false; _planComment.Set(""); FlushAnswer(); }
                    if (_plan?.Token.IsCancellationRequested == true) DecidePlan(false);
                    if (_pending == null && _plan == null && _question is { Question.Choices.Count: > 0 } choiceQuestion)
                    {
                        using var questionCancel = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, choiceQuestion.Token);
                        try
                        {
                            inlineDisplay?.Stop();
                            var response = FullScreenTerminal.Current is { } questionScreen ? await questionScreen.QuestionAsync(choiceQuestion.Question, questionCancel.Token)
                                : await TerminalQuestionPrompt.AskAsync(AnsiConsole.Console, reader, choiceQuestion.Question, questionCancel.Token);
                            AnswerQuestion(response);
                        }
                        catch (OperationCanceledException) { AnswerQuestion(null); }
                        finally { inlineDisplay?.Start(RenderActivity()); }
                    }
                    var hadInput = reader.HasInput;
                    if (hadInput)
                    {
                        TerminalInput input;
                        try { input = await reader.ReadAsync(cancelled.Token); } catch (OperationCanceledException) { input = default; }
                        var key = input.Key;
                        if (_pending == null && _question == null && _plan == null && FullScreenTerminal.Current?.InspectorInput(input) == true) { }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C && _pending == null && _questionText.Text.Length > 0 && _question != null) _questionText.Key(key, [], []);
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C && _pending == null && _question == null && _queued.HasSelection) _queued.Key(key, [], []);
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C && FullScreenTerminal.Current?.CopyTranscriptSelection(key) == true) { }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C) Stop();
                        else if (_question != null) { if (input.Wheel != 0) _questionScroll = Math.Max(0, _questionScroll - input.Wheel); else if (input.Paste != null) _questionText.Insert(input.Paste); else QuestionKey(key); }
                        else if (_plan != null) { if (input.Wheel != 0) _planScroll = Math.Max(0, _planScroll - input.Wheel); else if (input.Paste != null) { if (_planCommentActive) _planComment.Insert(input.Paste); } else PlanKey(key); }
                        else if (input.Paste != null && _pending == null)
                        {
                            var prepared = await PastedImages.PrepareAsync(input.Paste, Workspace, cancelled.Token);
                            _queued.InsertPaste(prepared.Text, prepared.Notice);
                        }
                        else if (_pending != null) { if (input.Wheel != 0) _approvalScroll = Math.Clamp(_approvalScroll - input.Wheel, 0, Math.Max(0, _pending.Preview.Lines.Count - 1)); else ApprovalKey(key); }
                        else if (FullScreenTerminal.Current is { } contextScreen && await contextScreen.ContextClickAsync(input, _queued, async text =>
                        { var prepared = await PastedImages.PrepareAsync(text, Workspace, cancelled.Token); _queued.InsertPaste(prepared.Text, prepared.Notice); })) { }
                        else if (FullScreenTerminal.Current?.SelectInput(input, _queued) == true) { }
                        else if (FullScreenTerminal.Current?.Navigation(input) == true) { }
                        else if (key.Key == ConsoleKey.Escape) Stop();
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.O)
                        {
                            // Ctrl+O expands the newest step detail: execution first, then thinking.
                            if (FullScreenTerminal.Current?.InspectLatest() != true)
                            {
                                if (_taskThought.Length > 0 && ShowThinking) FullScreenTerminal.Current?.InspectThought(Tail(_taskThought, 20000));
                                else FullScreenTerminal.Current?.InspectLatestThought();
                            }
                        }
                        else
                        {
                            var queued = _queued.Key(key, _queued.Suggestions(RunningHints), []);
                            if (!string.IsNullOrWhiteSpace(queued))
                            {
                                try
                                {
                                    if (Inbox == null) throw new InvalidOperationException(L.Pick("待处理输入收件箱未初始化。", "The pending-input inbox is not initialized."));
                                    if (queued == "/queue" || queued.StartsWith("/queue ", StringComparison.Ordinal))
                                    {
                                        var restored = await Inbox.CommandAsync(queued, "", cancelled.Token);
                                        _queued.Set(restored ?? "");
                                    }
                                    else if (queued.StartsWith('/') && !queued.StartsWith("/steer ", StringComparison.Ordinal))
                                    {
                                        if (runningCommand != null) throw new InvalidOperationException(L.Pick("上一条命令仍在处理，请先处理当前授权或回答。", "The previous command is still running; finish the current approval or answer first."));
                                        if (RunningCommand == null) throw new InvalidOperationException(L.Pick("运行中命令未初始化。", "The running-command handler is not initialized."));
                                        runningCommand = RunningCommand(queued, cancelled.Token);
                                        _queued.Set("");
                                    }
                                    else
                                    {
                                        var delivery = "steer";
                                        var prompt = queued;
                                        if (queued.StartsWith("/steer ", StringComparison.Ordinal)) { delivery = "steer"; prompt = queued[7..].Trim(); }
                                        var admitted = await Inbox.EnqueueAsync(prompt, delivery, cancelled.Token);
                                        _queued.Set(""); TerminalOutput.Write(new Text(L.Pick($"待处理输入已保存 · {admitted.Id.ToString()[..8]} · {delivery}\n", $"Pending input saved · {admitted.Id.ToString()[..8]} · {delivery}\n"), new Style(Color.Grey)));
                                    }
                                }
                                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                                { Error(ex.Message); }
                            }
                        }
                    }
                    var processed = 0;
                    while (next.IsCompleted && processed++ < 64)
                    {
                        var (has, evt) = await next;
                        if (!has || evt == null) { FlushAnswer(); return; }
                        await RenderAsync(evt, CancellationToken.None);
                        next = NextAsync();
                    }
                    // Constructing activity also formats reasoning, suggestions and pending input.
                    // Keep that work at display cadence; input still gets an immediate frame.
                    var now = Environment.TickCount64;
                    if (hadInput || now - lastRefresh >= 50)
                    {
                        refresh(RenderActivity()); lastRefresh = now;
                    }
                    if (!next.IsCompleted && !reader.HasInput) await Task.WhenAny(next, Task.Delay(8, CancellationToken.None));
                }
            }
            if (FullScreenTerminal.Current is { } screen)
                await PumpAsync(activity => { var prompt = _plan != null || _pending != null || _question != null; screen.SetActivity(prompt ? null : activity); screen.SetConversationPrompt(prompt ? new Panel(activity).Header(_plan != null ? L.Pick("计划审批", "Plan approval") : _pending != null ? L.Pick("需要授权", "Approval required") : L.Pick("需要你的回答", "Your answer is needed")).Border(BoxBorder.Rounded).BorderColor(Color.Yellow).Padding(1, 0) : null); screen.SetInput(_queued, prompt ? [] : _queued.Suggestions(RunningHints), _plan != null && _planCommentActive ? L.Pick("输入评论 / 建议 · Enter 提交并继续规划 · Esc 返回", "Type a comment / suggestion · Enter submits and keeps planning · Esc back") : prompt ? L.Pick("↑↓ 选择 · Enter 确认 · Esc 拒绝 / 返回 · 3 评论", "↑↓ select · Enter confirm · Esc reject / back · 3 comment") : L.Pick("任务运行中 · Esc 停止 · 回车指导当前任务 · /tasks 任务 · /queue add 排队", "Task running · Esc stop · Enter steer · /tasks tasks · /queue add queue")); screen.Draw(); });
            else
            {
                using var display = new SpectreScreenDisplay(AnsiConsole.Console); inlineDisplay = display; display.Start(RenderActivity());
                await PumpAsync(display.Update);
            }
        }
        finally
        {
            // Leaving the view only releases the channel; the run keeps its own cancellation.
            run?.Detach();
            _attachedRun = null;
            cancelled.Cancel();
            _live = false; _elapsed.Stop(); FlushAnswer(); Draft = _queued.Text;
            // Detaching with a dialog open leaves the request with the run: it is asked again when the
            // session comes back instead of being denied by accident.
            if (run is { IsFinished: false } && _pending is { } openApproval) { run.EnqueueApproval(openApproval); _pending = null; }
            if (run is { IsFinished: false } && _question is { } openQuestion) { run.EnqueueQuestion(openQuestion); _question = null; }
            if (run is { IsFinished: false } && _plan is { } openPlan) { run.EnqueuePlan(openPlan); _plan = null; }
            Decide(false); while (_approvals.TryDequeue(out var pending)) pending.Completion.TrySetResult(false);
            AnswerQuestion(null); while (_questions.TryDequeue(out var question)) question.Completion.TrySetResult(null);
            DecidePlan(false); while (_plans.TryDequeue(out var plan)) plan.Completion.TrySetResult(new PlanDecision(false));
            if (runningCommand != null)
            {
                try { await runningCommand; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error(ex.Message); }
            }
            FullScreenTerminal.Current?.SetActivity(null);
            FullScreenTerminal.Current?.SetDialog(null);
            FullScreenTerminal.Current?.SetConversationPrompt(null);
        }
    }

    private bool TryDequeueApproval(SessionRun? run, out PendingApproval? approval)
        => run != null ? run.TryDequeueApproval(out approval) : _approvals.TryDequeue(out approval);
    private bool TryDequeueQuestion(SessionRun? run, out PendingQuestion? question)
        => run != null ? run.TryDequeueQuestion(out question) : _questions.TryDequeue(out question);
    private bool TryDequeuePlan(SessionRun? run, out PendingPlan? plan)
        => run != null ? run.TryDequeuePlan(out plan) : _plans.TryDequeue(out plan);

    private void AnswerQuestion(string? answer)
    {
        var question = _question; _question = null;
        question?.Completion.TrySetResult(answer);
        if (question != null && _attachedRun is { } run && run.Waiting) { run.State = SessionRunState.Running; Runs?.Bump(); }
    }
    private void QuestionKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape) { AnswerQuestion(null); return; }
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        { _questionScroll = Math.Max(0, _questionScroll + (key.Key == ConsoleKey.UpArrow ? -2 : 2)); return; }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow && key.Modifiers == 0 && _question!.Question.Choices.Count > 0)
        { var count = _question.Question.Choices.Count; _questionChoice = Math.Clamp(_questionChoice + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, count - 1); return; }
        var submitted = _questionText.Key(key, [], []);
        if (submitted == null) return;
        if (!string.IsNullOrWhiteSpace(submitted)) AnswerQuestion(submitted);
        else if (_question!.Question.Choices.Count > 0) AnswerQuestion(_question.Question.Choices[_questionChoice]);
    }
    private void ApprovalKey(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Y or ConsoleKey.D1 or ConsoleKey.NumPad1) { Decide(true); return; }
        if (key.Key is ConsoleKey.N or ConsoleKey.D2 or ConsoleKey.NumPad2 or ConsoleKey.Escape) { Decide(false); return; }
        if (key.Key is ConsoleKey.A or ConsoleKey.D3 or ConsoleKey.NumPad3 && _pending!.Preview.RememberForSession != null) { _pending.Preview.RememberForSession(); Decide(true); return; }
        if (key.Key == ConsoleKey.Enter) { if (_approvalChoice == 2) _pending!.Preview.RememberForSession?.Invoke(); Decide(_approvalChoice != 1); return; }
        if (key.Key == ConsoleKey.Tab) _expanded = !_expanded;
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control)) _approvalScroll = Math.Clamp(_approvalScroll + (key.Key == ConsoleKey.UpArrow ? -3 : 3), 0, Math.Max(0, _pending!.Preview.Lines.Count - 1));
            else { var count = _pending!.Preview.RememberForSession == null ? 2 : 3; _approvalChoice = Math.Clamp(_approvalChoice + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, count - 1); }
        }
    }
    private void PlanKey(ConsoleKeyInfo key)
    {
        if (_planCommentActive)
        {
            if (key.Key == ConsoleKey.Enter) { DecidePlan(false, _planComment.Text); return; }
            if (key.Key == ConsoleKey.Escape || key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            { _planCommentActive = false; _planComment.Set(""); return; }
            _planComment.Key(key, [], []);
            return;
        }
        if (key.Key is ConsoleKey.Y or ConsoleKey.D1 or ConsoleKey.NumPad1) { DecidePlan(true); return; }
        if (key.Key is ConsoleKey.N or ConsoleKey.D2 or ConsoleKey.NumPad2 or ConsoleKey.Escape) { DecidePlan(false); return; }
        if (key.Key is ConsoleKey.D3 or ConsoleKey.NumPad3 || key.Key == ConsoleKey.C && key.Modifiers == 0)
        { _planChoice = 2; _planCommentActive = true; _planComment.Set(""); return; }
        if (key.Key == ConsoleKey.Enter)
        {
            if (_planChoice == 2) { _planCommentActive = true; _planComment.Set(""); }
            else DecidePlan(_planChoice == 0);
            return;
        }
        if (key.Key == ConsoleKey.Tab) { _planExpanded = !_planExpanded; return; }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                _planScroll = Math.Clamp(_planScroll + (key.Key == ConsoleKey.UpArrow ? -3 : 3), 0, Math.Max(0, _plan!.Preview.Lines.Count - 1));
            else _planChoice = Math.Clamp(_planChoice + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, 2);
        }
    }
    private void Decide(bool allow)
    {
        var pending = _pending; _pending = null;
        pending?.Completion.TrySetResult(allow);
        if (pending != null && _attachedRun is { } run && run.Waiting) { run.State = SessionRunState.Running; Runs?.Bump(); }
        if (pending != null) { TerminalOutput.MarkupLine(allow ? L.Pick("[dim]  ⎿ 本次操作已授权[/]", "[dim]  ⎿ Operation approved[/]") : L.Pick("[yellow]  ⎿ 本次操作已拒绝[/]", "[yellow]  ⎿ Operation rejected[/]")); _stage = allow ? Stage.Running : Stage.WaitingContinue; }
    }
    private void DecidePlan(bool approve, string? feedback = null)
    {
        var plan = _plan; _plan = null;
        _planCommentActive = false; _planComment.Set("");
        var comment = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        plan?.Completion.TrySetResult(new PlanDecision(approve, comment));
        if (plan != null && _attachedRun is { } run && run.Waiting) { run.State = SessionRunState.Running; Runs?.Bump(); }
        if (plan != null)
        {
            var label = approve ? L.Pick("[dim]  ⎿ 计划已批准[/]", "[dim]  ⎿ Plan approved[/]") : L.Pick("[yellow]  ⎿ 计划已拒绝 · 继续规划[/]", "[yellow]  ⎿ Plan rejected · continue planning[/]");
            TerminalOutput.MarkupLine(label + (comment is { Length: > 0 } ? " · " + Markup.Escape(comment) : ""));
            _stage = approve ? Stage.Running : Stage.WaitingPlanRevision;
        }
    }
    internal IRenderable RenderActivity()
    {
        if (_plan != null) return _plan.Preview.Render(_planChoice, _planScroll, _planExpanded, FullScreenTerminal.Current != null ? 6 : null, _planCommentActive ? _planComment : null);
        if (_pending != null) return _pending.Preview.Render(_approvalChoice, _approvalScroll, _expanded, FullScreenTerminal.Current != null ? 2 : null);
        if (_question != null)
        {
            var visibleChoices = Math.Min(_question.Question.Choices.Count, Math.Clamp(AnsiConsole.Profile.Height - 12, 1, 6));
            var questionHeight = Math.Clamp(AnsiConsole.Profile.Height - visibleChoices - 12, 1, 6);
            var questionRows = new List<IRenderable> { new Text(L.Pick("需要你的回答", "Your answer is needed"), new Style(Color.Yellow, decoration: Decoration.Bold)), new TextViewport(new Text(_question.Question.Question), questionHeight, _questionScroll) };
            foreach (var (choice, index) in _question.Question.Choices.Select((c, i) => (c, i)).Skip(Math.Max(0, _questionChoice - visibleChoices + 1)).Take(visibleChoices)) questionRows.Add(new Text((index == _questionChoice ? "› " : "  ") + choice, new Style(index == _questionChoice ? Color.Cyan1 : Color.Grey)).Crop());
            questionRows.Add(new TranscriptViewport([FullScreenTerminal.Input(_questionText)], 1, 0)); questionRows.Add(new Text(L.Pick("↑↓ 选择 · Enter 确认 · 输入其他回答 · Ctrl+↑↓ 详情 · Esc 跳过", "↑↓ select · Enter confirm · type another answer · Ctrl+↑↓ details · Esc skip"), new Style(Color.Grey)).Crop());
            return new Rows(questionRows);
        }
        var displayStage = _stage == Stage.ExecutingTool && _codeBlocks.Values.LastOrDefault()?.DelegationFinished == true
            ? L.Pick("子代理已完成 · 正在接收结果", "Child agent finished · receiving results")
            : StageLabel;
        var glyph = _stage == Stage.ExecutingTool ? LoadingIndicator.DotsFrame(_elapsed.ElapsedMilliseconds) : LoadingIndicator.Frame(_elapsed.ElapsedMilliseconds);
        var rows = new List<IRenderable>();
        if (_answer.Length > 0) rows.Add(new Text("● " + Tail(_answer)));
        if (ShowThinking && _taskThought.Length > 0)
        {
            rows.Add(new ThinkingLine(Tail(_taskThought, 4000), _elapsed.ElapsedMilliseconds, prefix: L.Pick("思考 ", "Thinking "), hint: L.Pick(" [Ctrl+O 展开]", " [Ctrl+O expand]")));
        }
        if (_stage != Stage.ExecutingTool || !_codeBlocks.Values.Any(block => block.Ok == null))
            rows.Add(new Markup($"[{AccentMarkup()}]{glyph} {Markup.Escape(displayStage)}…[/] [dim]({(int)_elapsed.Elapsed.TotalSeconds}s · {L.Pick($"{_toolCalls} 个工具", $"{_toolCalls} tools")})[/]"));
        rows.Add(new Text(L.Pick("  Esc 停止当前任务 · Ctrl+O 展开最新详情 · 回车指导任务 · /queue add 排队", "  Esc stop · Ctrl+O expand latest detail · Enter steer · /queue add queue"), new Style(Color.Grey)));
        if (Inbox?.Label is { Length: > 0 } inboxLabel) rows.Add(new Text("  " + inboxLabel + L.Pick(" · /steer 内容更新当前任务", " · /steer text updates the current task"), new Style(Color.Cyan1)));
        if (_queued.Text.Length > 0) rows.Add(new Text(L.Pick("  指导 › ", "  Steer › ") + _queued.Text, new Style(Color.Cyan1)));
        return new Rows(rows);
    }
    public async Task RenderAsync(AgentEvent evt, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { type = evt.GetType().Name, data = (object)evt }, ToolResult.JsonOptions));
                if (evt is AgentDone done) LastReason = done.FinishReason;
                return;
            }
            switch (evt)
            {
                case TokenDelta token:
                    FlushThinking();
                    _stage = Stage.Answering;
                    if (_live) _answer.Append(token.Text); else { TerminalOutput.Write(new Text(token.Text)); _plainStreaming = true; } break;
                case ThinkingDelta thought:
                    _stage = _processingChildResults ? Stage.HandlingChildResults : Stage.Thinking;
                    if (string.IsNullOrEmpty(thought.Text)) break;
                    if (_thought.Length == 0 && _taskThought.Length > 0 && _taskThought[^1] is not ('\n' or '\r')) _taskThought.Append('\n');
                    _thought.Append(thought.Text);
                    _taskThought.Append(thought.Text);
                    break;
                case AgentIteration iteration:
                    FlushThinking(); _thinkingAttemptStart = _taskThought.Length; _toolCalls = iteration.ToolCalls; _stage = _processingChildResults ? Stage.HandlingChildResults : Stage.Thinking;
                    if (!_live) { EndText(); TerminalOutput.MarkupLine("[#7dcfff]" + Markup.Escape(LoadingIndicator.Frame(_elapsed.ElapsedMilliseconds)) + L.Pick(" 正在思考[/]", " thinking[/]")); } break;
                case ToolCallStarted started:
                    _processingChildResults = false;
                    FlushThinking(); FlushAnswer(); EndText(); _stage = Stage.ExecutingTool;
                    if (started.ToolName == "execute_code")
                    {
                        using var args = JsonDocument.Parse(started.ArgsJson);
                        var block = new CodeExecutionBlock(started.Id, args.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "");
                        TrackCodeBlock(block); FullScreenTerminal.Current?.RegisterExecution(block);
                        if (FullScreenTerminal.Current != null) TerminalOutput.Write(block);
                    }
                    else
                    {
                        _toolCalls++;
                        if (started.ParentId is { } parent && _codeBlocks.TryGetValue(parent, out var block)) { block.Start(started); _hostBlocks[started.Id] = block; }
                        if (!_hostBlocks.ContainsKey(started.Id))
                        {
                            var standalone = new CodeExecutionBlock("host-" + started.Id, "");
                            standalone.Start(started); _hostBlocks[started.Id] = standalone;
                            FullScreenTerminal.Current?.RegisterExecution(standalone);
                            if (FullScreenTerminal.Current != null) TerminalOutput.Write(standalone);
                        }
                    }
                    break;
                case ToolCallRejected rejected:
                    FlushAnswer(); EndText();
                    TerminalOutput.Write(ToolFailure(L.Pick("未执行 " + rejected.ToolName + " · 模型应通过 execute_code 调用", "Not executed " + rejected.ToolName + " · the model must call tools through execute_code")));
                    _stage = Stage.CorrectingProtocol; break;
                case ToolCallCompleted completed:
                    if (completed.ToolName is "subagent" or "cancel_agent") { _processingChildResults = true; _stage = Stage.HandlingChildResults; }
                    if (_codeBlocks.TryGetValue(completed.Id, out var completedBlock))
                    {
                        completedBlock.Complete(completed);
                        if (FullScreenTerminal.Current == null) TerminalOutput.Write(completedBlock);
                        _stage = _processingChildResults ? Stage.HandlingChildResults : Stage.Thinking; break;
                    }
                    if (_hostBlocks.Remove(completed.Id, out var hostBlock))
                    {
                        hostBlock.Complete(completed);
                        if (hostBlock.Id == "host-" + completed.Id)
                        {
                            hostBlock.Complete(new(hostBlock.Id, "execute_code", completed.ResultJson));
                            if (FullScreenTerminal.Current == null) TerminalOutput.Write(hostBlock);
                        }
                    }
                    else
                    {
                        var standalone = new CodeExecutionBlock("host-" + completed.Id, "");
                        standalone.Start(new(completed.Id, completed.ToolName, "{}"));
                        standalone.Complete(completed);
                        standalone.Complete(new(standalone.Id, "execute_code", completed.ResultJson));
                        FullScreenTerminal.Current?.RegisterExecution(standalone);
                        TerminalOutput.Write(standalone);
                    }
                    _stage = Stage.Thinking; break;
                case AgentError error: FlushThinking(); FlushAnswer(); EndText(); Error(error.Message); break;
                case AgentInputAdmitted input:
                    FlushAnswer(); EndText();
                    if (FullScreenTerminal.Current is { } inputScreen) inputScreen.UserMessage(SessionTimeline.Text(input.Message));
                    else TerminalOutput.Write(TerminalMarkdown.Message(L.Pick("你", "You"), SessionTimeline.Text(input.Message), true));
                    if (input.Message.Images.Count > 0) TerminalOutput.Write(ImageAttachmentsView.Render(input.Message.Images));
                    _stage = Stage.ReceivingInput; break;
                case AgentNotice notice:
                    if (notice.ResetResponse) { _answer.Clear(); _thought.Clear(); _taskThought.Length = _thinkingAttemptStart; } else FlushAnswer();
                    EndText(); TerminalOutput.Write(new Text("↻ " + notice.Message + "\n", new Style(Color.Cyan1))); _stage = notice.ResetResponse ? Stage.WaitingRetry : Stage.AutoContinue; break;
                case AgentTokenUsage tokens:
                    _hasTurnUsage = true; TurnInputTokens += tokens.InputTokens; TurnOutputTokens += tokens.OutputTokens; break;
                case AgentContextUsage usage:
                    ContextUsedTokens = usage.UsedTokens; ContextCapacityTokens = usage.CapacityTokens; ContextEstimated = usage.Estimated; break;
                case AgentDone done:
                    FlushThinking(); FlushAnswer(); EndText(); LastReason = done.FinishReason;
                    var label = done.FinishReason switch
                    {
                        "stop" or "STOP" => L.Pick("完成", "Done"),
                        "cancelled" => L.Pick("已取消", "Cancelled"),
                        "timeout" => L.Pick("运行超时", "Timed out"),
                        "tool_limit" => L.Pick("已到工具上限", "Tool limit reached"),
                        "context_limit" => L.Pick("已到上下文上限", "Context limit reached"),
                        "protocol_error" => L.Pick("调用协议错误 · 已停止", "Tool protocol error · stopped"),
                        _ => done.FinishReason
                    };
                    TerminalOutput.Write(new Text($"\n  {label}" + (_live ? $" · {_elapsed.Elapsed.TotalSeconds:F1}s" : "") + "\n", new Style(Color.Grey))); break;
            }
        }
        finally { Gate.Release(); }
    }
    internal void TrackCodeBlock(CodeExecutionBlock block)
    {
        _codeBlocks[block.Id] = block;
        // Provider IDs can be reused. Never evict the newly registered block or
        // active executions, and keep host-call routing intact across pruning.
        foreach (var id in _codeBlocks.Where(pair => pair.Key != block.Id && pair.Value.Ok != null)
                     .Select(pair => pair.Key).ToArray())
        {
            if (_codeBlocks.Count <= 300) break;
            _codeBlocks.Remove(id);
        }
    }

    internal static string ToolLabel(string name, string argsJson)
    {
        if (name == "browser") return BrowserPresentation.Title(argsJson);
        if (name.StartsWith("computer_", StringComparison.Ordinal)) return ComputerUsePresentation.Title(name, argsJson);
        using var doc = JsonDocument.Parse(argsJson); var args = doc.RootElement;
        string Get(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return name switch
        {
            "memory_update" => L.Pick("学习本轮经验", "Learn from this turn"),
            "plan_propose" => L.Pick("提交计划 ", "Propose plan ") + Get("title"),
            "todo_read" => L.Pick("查看 TODO", "View TODO"), "todo_write" => L.Pick("更新 TODO", "Update TODO"),
            "everything_search" => L.Pick("Everything 搜索 ", "Everything search ") + Get("query") + " in " + Get("path"),
            "file_search" => L.Pick("搜索 ", "Search ") + Get("pattern") + " in " + Get("path"), "file_read" => L.Pick("读取 ", "Read ") + Get("path"), "file_write" => L.Pick("写入 ", "Write ") + Get("path"), "file_edit" => L.Pick("修改 ", "Edit ") + Get("path"), "list_directory" => L.Pick("查看目录 ", "List directory ") + Get("path"),
            "run_shell" => (args.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True ? L.Pick("后台命令 ", "Background command ") : L.Pick("运行命令 ", "Run command ")) + Get("exe") + (args.TryGetProperty("args", out var argv) ? " " + ToolResult.Clip(string.Join(' ', argv.EnumerateArray().Select(a => a.GetString())), 160) : ""),
            "cancel_agent" => L.Pick("停止子代理", "Stop child agent"),
            "shell_status" => L.Pick("后台命令进度", "Background command status"), "cancel_shell" => L.Pick("停止后台命令", "Stop background command"),
            "skill_read" => L.Pick("加载技能 /", "Load skill /") + Get("name"), "skill_list" => L.Pick("查看技能列表", "View skill list"), "skill_reference" => L.Pick("读取技能参考 ", "Read skill reference ") + Get("path"), _ => name.StartsWith("mcp_", StringComparison.Ordinal) ? "MCP " + name : name
        };
    }
    internal static Text ToolFailure(string message) => new("  ✕ " + message + "\n", new Style(Color.Red));
    internal static string ResultSummary(JsonElement result, bool details, string? toolName = null)
    {
        if (toolName?.StartsWith("computer_", StringComparison.Ordinal) == true) return ComputerUsePresentation.Summary(toolName, result);
        if (!result.TryGetProperty("data", out var data)) return L.Pick("完成", "Done");
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (toolName is "todo_read" or "todo_write" && data.TryGetProperty("todos", out var todos))
            {
                var items = todos.Deserialize<AllAgent.NET.Core.Models.TodoItem[]>(ToolResult.JsonOptions) ?? [];
                return "TODO · " + items.Count(t => t.Status == "completed") + "/" + items.Length +
                    (items.Length == 0 ? L.Pick(" · 暂无待办", " · no items") : "\n" + string.Join('\n', items.Select(TodoCommand.Line)));
            }
            if (toolName == "plan_propose")
            {
                var approvedPlan = data.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.True;
                var feedback = data.TryGetProperty("feedback", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null;
                return (approvedPlan ? L.Pick("计划已批准 · 已切换到完全访问", "Plan approved · switched to full access") : L.Pick("计划已拒绝 · 继续规划", "Plan rejected · continue planning"))
                    + (string.IsNullOrWhiteSpace(feedback) ? "" : " · " + feedback);
            }
            if (toolName == "browser" && data.TryGetProperty("browser", out var browser))
            {
                if (data.TryGetProperty("completed", out var filled) && filled.ValueKind == JsonValueKind.Array)
                    return L.Pick("已填写并核对 " + filled.GetArrayLength() + " 个字段；网站保存结果仍需验证", "Filled and checked " + filled.GetArrayLength() + " fields; verify the site's saved result");
                if (data.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String)
                    return (outcome.GetString() switch
                    {
                        "success_observed" => L.Pick("✓ 已观察到成功提示", "✓ Success message observed"),
                        "error_observed" => L.Pick("! 页面返回错误提示", "! The page returned an error"),
                        "unknown" => L.Pick("? 结果尚未确认，未自动重试", "? Result unconfirmed; not retried automatically"),
                        "dialog_pending" => L.Pick("等待处理网页对话框", "Waiting for a page dialog"),
                        _ => L.Pick("操作已发出，需验证网站结果", "Action sent; verify the site's result")
                    }) +
                        (data.TryGetProperty("message", out var message) ? " · " + ToolResult.Clip(message.ToString(), 220) : "");
                var name = browser.GetProperty("name").GetString() ?? L.Pick("浏览器", "Browser");
                var endpoint = browser.TryGetProperty("endpoint", out var address) && address.ValueKind == JsonValueKind.String ? " · " + address.GetString() : "";
                var page = data.TryGetProperty("url", out var url) ? " · " + url.GetString() : "";
                var profile = browser.TryGetProperty("profile", out var directory) && directory.ValueKind == JsonValueKind.String ? " · " + directory.GetString() : "";
                var feedback = data.TryGetProperty("actionMessages", out var messages) && messages.ValueKind == JsonValueKind.Array
                    ? messages.EnumerateArray().Where(m => m.TryGetProperty("text", out _)).Select(m => m.GetProperty("text").GetString()).LastOrDefault() : null;
                var target = data.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String ? L.Pick(" · 目标：", " · target: ") + targetElement.GetString() : "";
                return name + target + profile + endpoint + page + (feedback == null ? "" : L.Pick("\n页面提示：", "\nPage message: ") + ToolResult.Clip(feedback, 220));
            }
            if (toolName is "memory_update")
                return (data.TryGetProperty("summary", out var summary) ? summary.GetString() : L.Pick("完成", "Done")) +
                    (data.TryGetProperty("learned", out var learned) && learned.GetArrayLength() > 0 ? "\n" + string.Join("\n", learned.EnumerateArray().Select(line => "    " + line.GetString())) : "");
            if (data.TryGetProperty("matches", out var matches)) return L.Pick("找到 " + matches.GetArrayLength() + " 行匹配", matches.GetArrayLength() + " matching lines");
            if (data.TryGetProperty("characters", out var size)) return L.Pick("已写入 " + size + " 字符", "Wrote " + size + " characters");
            if (data.TryGetProperty("stdout", out var stdout) && stdout.ValueKind == JsonValueKind.String) return ToolResult.Clip(stdout.GetString(), details ? ToolResult.MaxOutputCharacters : 250).Trim();
            if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return details ? ToolResult.Clip(content.GetString()) : L.Pick("已读取 " + content.GetString()!.Length + " 字符", "Read " + content.GetString()!.Length + " characters");
            if (data.TryGetProperty("entries", out var entries)) return L.Pick("找到 " + entries.GetArrayLength() + " 个条目", entries.GetArrayLength() + " entries");
        }
        return details ? data.ToString() : L.Pick("完成", "Done");
    }
    public void Error(string text)
    {
        if (Json) Console.Error.WriteLine(JsonSerializer.Serialize(new { error = text })); else TerminalOutput.Write(new Text("! " + text + "\n", new Style(Color.Red)));
    }
    internal static string Tail(StringBuilder builder, int maximum = 6000) => builder.Length <= maximum ? builder.ToString() : "…\n" + builder.ToString(builder.Length - maximum, maximum);
    internal static string AccentMarkup()
    {
        var color = FullScreenTerminal.Current?.Theme.Accent ?? Color.Cyan1;
        return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
    }
    private void FlushThinking()
    {
        if (_thought.Length == 0) return;
        if (ShowThinking)
        {
            var block = new CodeExecutionBlock("thought-" + Guid.NewGuid().ToString("N"), _thought.ToString(), reasoning: true);
            FullScreenTerminal.Current?.RegisterExecution(block);
            TerminalOutput.Write(block);
            if (FullScreenTerminal.Current == null) TerminalOutput.WriteLine();
        }
        _thought.Clear();
    }
    private void FlushAnswer() { if (_answer.Length == 0) return; TerminalOutput.Write(TerminalMarkdown.Message("AllAgent", _answer.ToString(), false)); TerminalOutput.WriteLine(); _answer.Clear(); }
    private void EndText() { if (_plainStreaming) { TerminalOutput.WriteLine(); _plainStreaming = false; } }
}
