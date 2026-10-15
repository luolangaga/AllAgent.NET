using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

internal sealed class ToolsApproval(TerminalUi ui)
{
    public bool PlanMode { get; set; }
    public bool FullAccess { get; set; }
    public string ModeLabel => PlanMode ? "只读规划" : FullAccess ? "完全访问" : "逐次授权";
    public WorkspaceCheckpoints? Checkpoints { get; set; }
    public async ValueTask<bool> ApproveAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        if (PlanMode && request.Risk != ToolRisk.Read) return false;
        if (PlanMode && request.Name == "external_directory" && request.Arguments.GetProperty("operation").GetString() is not ("file_read" or "file_search" or "file_glob" or "list_directory")) return false;
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
    private readonly EscapeCancelConfirmation _cancelConfirmation = new();
    public InputInbox? Inbox { get; set; }
    public string Draft { get; set; } = "";
    public string LastReason { get; private set; } = "stop";
    public int ContextUsedTokens { get; private set; }
    public int ContextCapacityTokens { get; private set; } = 100000;
    public bool ContextEstimated { get; private set; } = true;
    private string? _contextModel;
    public string ContextLabel => "上下文 " + (ContextEstimated ? "≈" : "") + LanAgent.Core.Services.AI.ModelContext.Format(ContextUsedTokens) + " / " + LanAgent.Core.Services.AI.ModelContext.Format(ContextCapacityTokens);
    public void SetModelContext(LanAgent.Core.Models.ApiConfig config)
    {
        var key = config.Id + ":" + config.DefaultModel;
        if (_contextModel != key) { ContextUsedTokens = 0; ContextEstimated = true; _contextModel = key; }
        ContextCapacityTokens = LanAgent.Core.Services.AI.ModelContext.Capacity(config);
    }
    public void RestoreContextUsage(LanAgent.Core.Models.Conversation? conversation)
    {
        if (conversation?.ContextUsedTokens is { } used && conversation.ContextUsageModel == conversation.ModelId)
        { ContextUsedTokens = used; ContextEstimated = conversation.ContextUsageEstimated; }
        else { ContextUsedTokens = (conversation?.Messages.Sum(message => message.Content.Length + (message.ToolCallsJson?.Length ?? 0)) ?? 0) / 4; ContextEstimated = true; }
    }
    private readonly ConcurrentQueue<PendingApproval> _approvals = new();
    private readonly ConcurrentQueue<PendingQuestion> _questions = new();
    private readonly StringBuilder _answer = new();
    private readonly StringBuilder _thought = new();
    private readonly Stopwatch _elapsed = new();
    private string _stage = "思考中";
    private int _toolCalls;
    private volatile bool _live;
    public bool AgentIsLive => _live;
    private PendingApproval? _pending;
    private PendingQuestion? _question;
    private int _questionChoice;
    private int _questionScroll;
    private InputState _questionText = new();
    private int _approvalChoice = 0, _approvalScroll;
    private bool _expanded;
    private InputState _queued = new();
    private bool _plainStreaming;
    private readonly Dictionary<string, CodeExecutionBlock> _codeBlocks = [];
    private readonly Dictionary<string, CodeExecutionBlock> _hostBlocks = [];
    private sealed record PendingApproval(ApprovalPreview Preview, TaskCompletionSource<bool> Completion, CancellationToken Token);
    private sealed record PendingQuestion(AgentQuestion Question, TaskCompletionSource<string?> Completion, CancellationToken Token);

    public async Task<string?> RequestQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        if (!Interactive || Json) return null;
        if (!_live)
        {
            if (question.Choices.Count > 0) return FullScreenTerminal.Current is { } questionScreen ? await questionScreen.QuestionAsync(question, ct) : await TerminalQuestionPrompt.AskAsync(AnsiConsole.Console, new TerminalKeyReader(), question, ct);
            if (FullScreenTerminal.Current is { } screen) return await screen.TextAsync("任务需要你的回答", null, false, true, ct, new Text(question.Question + "\n" + string.Join("\n", question.Choices)));
            TerminalOutput.Write(new Panel(new Text(question.Question)).Header("需要你的回答"));
            return await ManagementUi.Text("请输入回答");
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
        TerminalOutput.Write(new Markup("[bold #d97757]▐▛███▜▌[/]  [bold]LanAgent[/]  [dim]CodeAct · Hyperlight[/]\n"));
        TerminalOutput.Write(new Text($"  {model} · 会话 {session.ToString()[..8]}\n  {workspace}\n", new Style(Color.Grey)));
        TerminalOutput.MarkupLine("[dim]  / 命令 · /providers 模型 · /skills 技能 · /mcp 扩展 · Ctrl+C 取消[/]");
    }
    public async ValueTask<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var preview = ApprovalPreview.Create(request, Workspace);
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
            return await AnsiConsole.PromptAsync(new ConfirmationPrompt("允许这一次操作？") { DefaultValue = true }, ct);
        }
        finally { Gate.Release(); }
    }
    public async Task RunAgentAsync(IAsyncEnumerable<AgentEvent> events, CancellationToken ct)
    {
        if (Json || !Interactive || !AnsiConsole.Profile.Capabilities.Ansi)
        {
            await foreach (var evt in events) await RenderAsync(evt, CancellationToken.None); return;
        }
        using var inputMode = new TextInputMode();
        _cancelConfirmation.Reset();
        _answer.Clear(); _thought.Clear(); _stage = "思考中"; _toolCalls = 0; _elapsed.Restart(); _live = true;
        _queued = new InputState(); _queued.Set(Draft); Draft = "";
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            SpectreScreenDisplay? inlineDisplay = null;
            async Task PumpAsync(Action<IRenderable> refresh)
            {
                var reader = FullScreenTerminal.Current?.Keys ?? new TerminalKeyReader();
                var queueShortcut = new QueueShortcut();
                await using var enumerator = events.GetAsyncEnumerator(cancelled.Token);
                var next = enumerator.MoveNextAsync().AsTask();
                while (true)
                {
                    if (_pending == null && _approvals.TryDequeue(out var approval)) { _pending = approval; _approvalChoice = 0; _approvalScroll = 0; _expanded = false; FlushAnswer(); }
                    if (_pending?.Token.IsCancellationRequested == true) Decide(false);
                    if (_question == null && _questions.TryDequeue(out var question)) { _question = question; _questionChoice = _questionScroll = 0; _questionText = new(); FlushAnswer(); }
                    if (_question?.Token.IsCancellationRequested == true) AnswerQuestion(null);
                    if (_pending == null && _question is { Question.Choices.Count: > 0 } choiceQuestion)
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
                    if (reader.HasInput)
                    {
                        TerminalInput input;
                        try { input = await reader.ReadAsync(cancelled.Token); } catch (OperationCanceledException) { input = default; }
                        var key = input.Key;
                        if (key.Key != 0 && key.Key != ConsoleKey.Escape || input.Paste != null || input.Pointer != null || input.Wheel != 0 || _pending != null || _question != null) _cancelConfirmation.Reset();
                        var editing = _question != null ? _questionText : _queued;
                        var queueChord = _pending == null && _question == null && input.Paste == null ? queueShortcut.Read(key, editing.HasSelection) : 0;
                        if (queueChord == 1) { }
                        else if (queueChord == 2 && Inbox != null)
                        {
                            var restored = await Inbox.CommandAsync("/queue", _queued.Text, cancelled.Token);
                            if (restored != null) { _queued.SelectAll(); _queued.Insert(restored); }
                        }
                        else if (_pending == null && _question == null && FullScreenTerminal.Current?.InspectorInput(input) == true) { }
                        else if (_pending == null && _question == null && key.Key == ConsoleKey.E && key.Modifiers.HasFlag(ConsoleModifiers.Control) && FullScreenTerminal.Current?.InspectLatest() == true) { }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C && _pending == null && editing.HasSelection) editing.Key(key, [], []);
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C && FullScreenTerminal.Current?.CopyTranscriptSelection(key) == true) { }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C) cancelled.Cancel();
                        else if (_question != null) { if (input.Wheel != 0) _questionScroll = Math.Max(0, _questionScroll - input.Wheel); else if (input.Paste != null) _questionText.Insert(input.Paste); else QuestionKey(key); }
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
                        else if (key.Key == ConsoleKey.Escape)
                        {
                            if (_cancelConfirmation.Read(key)) cancelled.Cancel();
                        }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.O) { if (_thought.Length > 0 && ShowThinking) FullScreenTerminal.Current?.InspectThought(_thought.ToString()); else FullScreenTerminal.Current?.InspectLatestThought(); }
                        else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C) cancelled.Cancel();
                        else
                        {
                            var queued = _queued.Key(key, [], []);
                            if (!string.IsNullOrWhiteSpace(queued))
                            {
                                try
                                {
                                    if (Inbox == null) throw new InvalidOperationException("待处理输入收件箱未初始化。");
                                    if (queued == "/queue" || queued.StartsWith("/queue ", StringComparison.Ordinal))
                                    {
                                        var restored = await Inbox.CommandAsync(queued, "", cancelled.Token);
                                        _queued.Set(restored ?? "");
                                    }
                                    else
                                    {
                                        var delivery = "queue";
                                        var prompt = queued;
                                        if (queued.StartsWith("/steer ", StringComparison.Ordinal)) { delivery = "steer"; prompt = queued[7..].Trim(); }
                                        else if (queued.StartsWith('/')) throw new ArgumentException("任务运行中可用 /queue 管理输入，或 /steer 内容 更新当前任务；其他命令请在任务结束后使用。");
                                        var admitted = await Inbox.EnqueueAsync(prompt, delivery, cancelled.Token);
                                        _queued.Set(""); TerminalOutput.Write(new Text($"待处理输入已保存 · {admitted.Id.ToString()[..8]} · {delivery}\n", new Style(Color.Grey)));
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
                        if (!await next) { FlushAnswer(); return; }
                        await RenderAsync(enumerator.Current, CancellationToken.None);
                        next = enumerator.MoveNextAsync().AsTask();
                    }
                    refresh(RenderActivity());
                    if (!next.IsCompleted && !reader.HasInput) await Task.WhenAny(next, Task.Delay(8, CancellationToken.None));
                }
            }
            if (FullScreenTerminal.Current is { } screen)
                await PumpAsync(activity => { var prompt = _pending != null || _question != null; screen.SetActivity(prompt ? null : activity); screen.SetConversationPrompt(prompt ? new Panel(activity).Header(_pending != null ? "需要授权" : "需要你的回答").Border(BoxBorder.Rounded).BorderColor(Color.Yellow).Padding(1, 0) : null); screen.SetInput(_queued, [], prompt ? "↑↓ 选择 · Enter 确认 · Esc 拒绝 / 返回" : _cancelConfirmation.Armed ? "再次按 Esc 确认停止当前任务" : "任务运行中 · 下一条输入可排队 · 双击 Esc 停止"); screen.Draw(); });
            else
            {
                using var display = new SpectreScreenDisplay(AnsiConsole.Console); inlineDisplay = display; display.Start(RenderActivity());
                await PumpAsync(display.Update);
            }
        }
        finally
        {
            _live = false; _elapsed.Stop(); FlushAnswer(); Draft = _queued.Text;
            Decide(false); while (_approvals.TryDequeue(out var pending)) pending.Completion.TrySetResult(false);
            AnswerQuestion(null); while (_questions.TryDequeue(out var question)) question.Completion.TrySetResult(null);
            FullScreenTerminal.Current?.SetActivity(null);
            FullScreenTerminal.Current?.SetDialog(null);
            FullScreenTerminal.Current?.SetConversationPrompt(null);
        }
    }
    private void AnswerQuestion(string? answer) { var question = _question; _question = null; question?.Completion.TrySetResult(answer); }
    private void QuestionKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape) { AnswerQuestion(null); return; }
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        { _questionScroll = Math.Max(0, _questionScroll + (key.Key == ConsoleKey.UpArrow ? -2 : 2)); return; }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow && key.Modifiers == 0 && _question!.Question.Choices.Count > 0)
        { var count = _question.Question.Choices.Count; _questionChoice = (_questionChoice + (key.Key == ConsoleKey.UpArrow ? -1 : 1) + count) % count; return; }
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
            else { var count = _pending!.Preview.RememberForSession == null ? 2 : 3; _approvalChoice = (_approvalChoice + (key.Key == ConsoleKey.UpArrow ? -1 : 1) + count) % count; }
        }
    }
    private void Decide(bool allow)
    {
        var pending = _pending; _pending = null;
        pending?.Completion.TrySetResult(allow);
        if (pending != null) { TerminalOutput.MarkupLine(allow ? "[dim]  ⎿ 本次操作已授权[/]" : "[yellow]  ⎿ 本次操作已拒绝[/]"); _stage = allow ? "执行中" : "等待模型继续"; }
    }
    internal IRenderable RenderActivity()
    {
        if (_pending != null) return _pending.Preview.Render(_approvalChoice, _approvalScroll, _expanded, FullScreenTerminal.Current != null ? 2 : null);
        if (_question != null)
        {
            var visibleChoices = Math.Min(_question.Question.Choices.Count, Math.Clamp(AnsiConsole.Profile.Height - 12, 1, 6));
            var questionHeight = Math.Clamp(AnsiConsole.Profile.Height - visibleChoices - 12, 1, 6);
            var questionRows = new List<IRenderable> { new Text("需要你的回答", new Style(Color.Yellow, decoration: Decoration.Bold)), new TextViewport(new Text(_question.Question.Question), questionHeight, _questionScroll) };
            foreach (var (choice, index) in _question.Question.Choices.Select((c, i) => (c, i)).Skip(Math.Max(0, _questionChoice - visibleChoices + 1)).Take(visibleChoices)) questionRows.Add(new Text((index == _questionChoice ? "› " : "  ") + choice, new Style(index == _questionChoice ? Color.Cyan1 : Color.Grey)).Crop());
            questionRows.Add(new TranscriptViewport([FullScreenTerminal.Input(_questionText)], 1, 0)); questionRows.Add(new Text("↑↓ 选择 · Enter 确认 · 输入其他回答 · Ctrl+↑↓ 详情 · Esc 跳过", new Style(Color.Grey)).Crop());
            return new Rows(questionRows);
        }
        var glyph = LoadingIndicator.Frame(_elapsed.ElapsedMilliseconds);
        var rows = new List<IRenderable>();
        if (_answer.Length > 0) rows.Add(new Text("● " + _answer));
        if (ShowThinking && _thought.Length > 0)
        {
            rows.Add(new ThinkingLine(_thought.ToString(), _elapsed.ElapsedMilliseconds, hint: " [Ctrl+O 展开]"));
        }
        rows.Add(new Markup($"[#7dcfff]{glyph} {Markup.Escape(_stage)}…[/] [dim]({(int)_elapsed.Elapsed.TotalSeconds}s · {_toolCalls} 个工具)[/]"));
        rows.Add(new Text(_cancelConfirmation.Armed ? "  再次按 Esc 确认停止当前任务" : "  双击 Esc / Ctrl+C 停止 · Ctrl+O 思考详情 · Ctrl+E 执行详情 · 输入下一条任务并回车可排队", new Style(Color.Grey)));
        if (Inbox?.Label is { Length: > 0 } inboxLabel) rows.Add(new Text("  " + inboxLabel + " · /steer 内容更新当前任务", new Style(Color.Cyan1)));
        if (_queued.Text.Length > 0) rows.Add(new Text("  下一条 › " + _queued.Text, new Style(Color.Cyan1)));
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
                    _stage = "回答中";
                    if (_live) _answer.Append(token.Text); else { TerminalOutput.Write(new Text(token.Text)); _plainStreaming = true; } break;
                case ThinkingDelta thought:
                    _stage = "思考中";
                    _thought.Append(thought.Text);
                    break;
                case AgentIteration iteration:
                    FlushThinking(); _toolCalls = iteration.ToolCalls; _stage = "思考中";
                    if (!_live) { EndText(); TerminalOutput.MarkupLine("[#7dcfff]" + Markup.Escape(LoadingIndicator.Frame(_elapsed.ElapsedMilliseconds)) + " 正在思考[/]"); } break;
                case ToolCallStarted started:
                    FlushThinking(); FlushAnswer(); EndText(); _stage = "执行工具";
                    if (started.ToolName == "execute_code")
                    {
                        using var args = JsonDocument.Parse(started.ArgsJson);
                        var block = new CodeExecutionBlock(started.Id, args.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "");
                        _codeBlocks[started.Id] = block; FullScreenTerminal.Current?.RegisterExecution(block);
                        if (FullScreenTerminal.Current != null) TerminalOutput.Write(block);
                        if (_codeBlocks.Count > 300) { _codeBlocks.Remove(_codeBlocks.Keys.First()); _hostBlocks.Clear(); }
                    }
                    else
                    {
                        _toolCalls++;
                        if (started.ParentId is { } parent && _codeBlocks.TryGetValue(parent, out var block)) { block.Start(started); _hostBlocks[started.Id] = block; }
                        if (!_hostBlocks.ContainsKey(started.Id)) TerminalOutput.Write(new Markup("[bold]● " + Markup.Escape(ToolLabel(started.ToolName, started.ArgsJson)) + "[/]\n"));
                    }
                    break;
                case ToolCallRejected rejected:
                    FlushAnswer(); EndText();
                    TerminalOutput.Write(ToolFailure("未执行 " + rejected.ToolName + " · 模型应通过 execute_code 调用"));
                    _stage = "纠正调用协议"; break;
                case ToolCallCompleted completed:
                    if (_codeBlocks.TryGetValue(completed.Id, out var completedBlock))
                    {
                        completedBlock.Complete(completed);
                        if (FullScreenTerminal.Current == null) TerminalOutput.Write(completedBlock);
                        _stage = "思考中"; break;
                    }
                    if (_hostBlocks.Remove(completed.Id, out var hostBlock)) { hostBlock.Complete(completed); _stage = "思考中"; break; }
                    EndText(); using (var result = JsonDocument.Parse(completed.ResultJson))
                    {
                        var root = result.RootElement; var ok = root.TryGetProperty("ok", out var success) && success.GetBoolean();
                        if (!ok) { var error = root.TryGetProperty("error", out var message) ? message.GetString() : "操作失败"; TerminalOutput.Write(ToolFailure(error ?? "操作失败")); }
                        else if (completed.ToolName != "execute_code" || Verbose)
                        {
                            var summary = ResultSummary(root, Verbose, completed.ToolName);
                            TerminalOutput.Write(new Text("  ⎿ " + summary + "\n", new Style(Color.Grey)));
                        }
                    }
                    _stage = "思考中"; break;
                case AgentError error: FlushThinking(); FlushAnswer(); EndText(); Error(error.Message); break;
                case AgentInputAdmitted input:
                    FlushAnswer(); EndText();
                    if (FullScreenTerminal.Current is { } inputScreen) inputScreen.UserMessage(SessionTimeline.Text(input.Message));
                    else TerminalOutput.Write(TerminalMarkdown.Message("你", SessionTimeline.Text(input.Message), true));
                    if (input.Message.Images.Count > 0) TerminalOutput.Write(ImageAttachmentsView.Render(input.Message.Images));
                    _stage = "收到新输入"; break;
                case AgentNotice notice:
                    if (notice.ResetResponse) { _answer.Clear(); _thought.Clear(); } else FlushAnswer();
                    EndText(); TerminalOutput.Write(new Text("↻ " + notice.Message + "\n", new Style(Color.Cyan1))); _stage = notice.ResetResponse ? "等待网络重试" : "自动继续"; break;
                case AgentContextUsage usage:
                    ContextUsedTokens = usage.UsedTokens; ContextCapacityTokens = usage.CapacityTokens; ContextEstimated = usage.Estimated; break;
                case AgentDone done:
                    FlushThinking(); FlushAnswer(); EndText(); LastReason = done.FinishReason;
                    var label = done.FinishReason switch { "stop" or "STOP" => "完成", "cancelled" => "已取消", "timeout" => "运行超时", "tool_limit" => "已到工具上限", "context_limit" => "已到上下文上限", "protocol_error" => "调用协议错误 · 已停止", _ => done.FinishReason };
                    TerminalOutput.Write(new Text($"\n  {label}" + (_live ? $" · {_elapsed.Elapsed.TotalSeconds:F1}s" : "") + "\n", new Style(Color.Grey))); break;
            }
        }
        finally { Gate.Release(); }
    }
    internal static string ToolLabel(string name, string argsJson)
    {
        if (name.StartsWith("computer_", StringComparison.Ordinal)) return ComputerUsePresentation.Title(name, argsJson);
        using var doc = JsonDocument.Parse(argsJson); var args = doc.RootElement;
        string Get(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return name switch
        {
            "memory_update" => "学习本轮经验", "author_profile_update" => "维护作者档案",
            "file_search" => "搜索 " + Get("pattern") + " in " + Get("path"), "file_read" => "读取 " + Get("path"), "file_write" => "写入 " + Get("path"), "file_edit" => "修改 " + Get("path"), "list_directory" => "查看目录 " + Get("path"),
            "run_shell" => (args.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True ? "后台命令 " : "运行命令 ") + Get("exe") + (args.TryGetProperty("args", out var argv) ? " " + ToolResult.Clip(string.Join(' ', argv.EnumerateArray().Select(a => a.GetString())), 160) : ""),
            "start_agent" => "启动子代理", "agent_status" => "子代理进度", "wait_agent" => "等待子代理", "cancel_agent" => "停止子代理",
            "shell_status" => "后台命令进度", "cancel_shell" => "停止后台命令",
            "skill_read" => "加载技能 /" + Get("name"), "skill_list" => "查看技能列表", "skill_reference" => "读取技能参考 " + Get("path"), _ => name.StartsWith("mcp_", StringComparison.Ordinal) ? "MCP " + name : name
        };
    }
    internal static Text ToolFailure(string message) => new("  ✕ " + message + "\n", new Style(Color.Red));
    internal static string ResultSummary(JsonElement result, bool details, string? toolName = null)
    {
        if (toolName?.StartsWith("computer_", StringComparison.Ordinal) == true) return ComputerUsePresentation.Summary(toolName, result);
        if (!result.TryGetProperty("data", out var data)) return "完成";
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (toolName is "memory_update" or "author_profile_update")
                return (data.TryGetProperty("summary", out var summary) ? summary.GetString() : "完成") +
                    (data.TryGetProperty("learned", out var learned) && learned.GetArrayLength() > 0 ? "\n" + string.Join("\n", learned.EnumerateArray().Select(line => "    " + line.GetString())) : "");
            if (data.TryGetProperty("matches", out var matches)) return "找到 " + matches.GetArrayLength() + " 行匹配";
            if (data.TryGetProperty("characters", out var size)) return "已写入 " + size + " 字符";
            if (data.TryGetProperty("stdout", out var stdout) && stdout.ValueKind == JsonValueKind.String) return ToolResult.Clip(stdout.GetString(), details ? ToolResult.MaxOutputCharacters : 250).Trim();
            if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return details ? ToolResult.Clip(content.GetString()) : "已读取 " + content.GetString()!.Length + " 字符";
            if (data.TryGetProperty("entries", out var entries)) return "找到 " + entries.GetArrayLength() + " 个条目";
        }
        return details ? data.ToString() : "完成";
    }
    public void Error(string text)
    {
        if (Json) Console.Error.WriteLine(JsonSerializer.Serialize(new { error = text })); else TerminalOutput.Write(new Text("! " + text + "\n", new Style(Color.Red)));
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
    private void FlushAnswer() { if (_answer.Length == 0) return; TerminalOutput.Write(TerminalMarkdown.Message("LanAgent", _answer.ToString(), false)); TerminalOutput.WriteLine(); _answer.Clear(); }
    private void EndText() { if (_plainStreaming) { TerminalOutput.WriteLine(); _plainStreaming = false; } }
}
