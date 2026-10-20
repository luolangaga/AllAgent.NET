using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

/// <summary>Spectre.Console layout with row-diff output; standard prompts share one output owner.</summary>
internal sealed class FullScreenTerminal : IDisposable
{
    public static FullScreenTerminal? Current { get; private set; }
    private readonly DataStore _store;
    private readonly SkillCatalog _skills;
    private readonly McpToolService _mcp;
    private readonly TerminalUi _ui;
    private readonly List<IRenderable> _transcript = [];
    private readonly Dictionary<Guid, IRenderable> _messageAnchors = [];
    private IRenderable? _jumpTarget;
    private readonly Dictionary<string, CodeExecutionBlock> _executions = [];
    private readonly Dictionary<(int X, int Y), (CodeExecutionBlock Block, int Span)> _traceCells = [];
    private (CodeExecutionBlock Block, int Span)? _inspector;
    private readonly Stack<((CodeExecutionBlock Block, int Span) Inspector, int Scroll, int Selected)> _inspectorHistory = [];
    private int _inspectorScroll, _inspectorSelection;
    private TextViewport? _inspectorViewport;
    private (CodeExecutionBlock Block, int Span, int Selected, int Version)? _inspectorCacheKey;
    private (int X, int Y)? _detailStart, _detailEnd;
    private bool _detailDragging;
    private int _detailPointerX, _detailPointerY;
    private long _detailScrollTick;
    private int _detailTop, _detailLeft;

    private void ClearDetailSelection() { _detailStart = _detailEnd = null; _detailDragging = false; }
    private bool DetailPointer(TerminalInput input)
    {
        if (input.Pointer is not { Button: PointerButton.Left } pointer || _inspectorViewport is not { } view) return false;
        _detailPointerX = pointer.X; _detailPointerY = pointer.Y;
        var inside = pointer.X >= _detailLeft && pointer.X < _detailLeft + view.Width && pointer.Y >= _detailTop && pointer.Y < _detailTop + view.Height;
        if (pointer.Action == PointerAction.Down)
        {
            ClearDetailSelection();
            if (!inside) return true;
            _detailDragging = true;
            _detailStart = _detailEnd = DetailPoint();
        }
        else if (_detailDragging)
        {
            _detailEnd = DetailPoint();
            if (pointer.Action == PointerAction.Up)
            {
                _detailDragging = false;
                if (_detailStart == _detailEnd) InspectPointer(input with { Pointer = pointer with { Action = PointerAction.Down } });
            }
        }
        return true;
    }
    private (int X, int Y) DetailPoint() =>
        (Math.Clamp(_detailPointerX - _detailLeft, 0, Math.Max(0, _inspectorViewport!.Width - 1)),
         _inspectorScroll + Math.Clamp(_detailPointerY - _detailTop, 0, _inspectorViewport.Height - 1));
    internal void AutoScrollDetail(long now)
    {
        if (!_detailDragging || _inspectorViewport is not { } view || now - _detailScrollTick < 50) return;
        var delta = _detailPointerY <= _detailTop ? -1 : _detailPointerY >= _detailTop + view.Height - 1 ? 1 : 0;
        if (delta == 0) return;
        _detailScrollTick = now;
        _inspectorScroll = Math.Clamp(_inspectorScroll + delta, 0, Math.Max(0, view.Lines.Count - view.Height));
        _detailEnd = DetailPoint();
    }
    private readonly IAnsiConsole _native;
    private readonly object _drawGate = new();
    private readonly SpectreScreenDisplay _display;
    private readonly TerminalKeyReader _keys = new();
    internal TerminalKeyReader Keys => _keys;
    private int? _lastFrameKey;
    private IRenderable? _cachedSidebar;
    private long _sidebarTime;
    private Segment[]? _lastSegments;
    private int _lastWidth, _lastHeight;
    private DateTime _skillsChecked;
    private int _enabledSkills;
    private string _status = L.Pick("准备就绪", "Ready");
    private IRenderable? _activity, _dialog, _conversationPrompt;
    private bool _busyDialogActive;
    private InputState _input = new();
    private readonly InputState _promptInput = new();
    private InputText? _inputText;
    private int _inputX, _inputY, _inputWidth;
    private int _hintsTop, _hintsHeight, _hintStart;
    private bool _dragSelecting;
    private (int X, int Y)? _transcriptSelectionStart, _transcriptSelectionEnd;
    private bool _dragSelectingTranscript;
    private SelectionRegion? _selectionRegion;
    private bool _outputFocused;
    private readonly List<SelectionRegion> _visibleBlocks = [];
    private IReadOnlyList<List<Segment>>? _selectionLines;
    private int _transcriptTop, _transcriptHeight, _transcriptLeft, _transcriptRight;
    private string? _selectionNotice;
    private IReadOnlyList<CommandHint> _hints = [];
    private int _scroll;
    private int _lastRows, _lastTranscriptWidth;
    private int PreserveScroll(int rows, int width)
    {
        if (_scroll > 0 && width == _lastTranscriptWidth) _scroll = Math.Max(0, _scroll + rows - _lastRows);
        _lastRows = rows; _lastTranscriptWidth = width; return _scroll;
    }
    private bool _entered, _disposed;
    private CancellationTokenSource? _promptCancellation;
    private readonly ConsoleCancelEventHandler _cancelHandler;
    public bool ShowSidebar { get; set; } = true;
    public Func<string>? ExtensionSummary { get; set; }
    public Func<string>? ModelFooter { get; set; }
    public Func<string>? TaskNotice { get; set; }
    public Func<string, BackgroundTaskInfo?>? TaskLookup { get; set; }
    public Func<string, IReadOnlyList<BackgroundTaskInfo>>? InvocationTasks { get; set; }
    public Func<long>? TaskRevision { get; set; }
    /// <summary>Main-session runs of this workspace: the sidebar marks them and the dock lists them.</summary>
    public SessionRuns? Runs { get; set; }
    public Func<int>? RunRevision { get; set; }
    /// <summary>Runs living in the other workspaces this process has open, so they stay visible here.</summary>
    public Func<IReadOnlyList<(string Name, int Running, int Waiting)>>? OtherWorkspaces { get; set; }
    private int _sidebarRunRevision = -1;
    private long _sidebarTodoRevision = -1;
    private long _sidebarPlanRevision = -1;
    public Func<BackgroundTaskInfo, IRenderable>? TaskTranscript { get; set; }
    public Func<BackgroundTaskInfo, IRenderable>? TaskPreview { get; set; }
    public Guid SessionId { get; private set; }
    public string Model { get; private set; } = L.Pick("未选择模型", "No model selected");
    public TerminalTheme Theme { get; set; }
    private Color Background => Theme.Background;
    private Color Surface => Theme.Surface;
    private Color Accent => Theme.Accent;
    public FullScreenTerminal(DataStore store, SkillCatalog skills, McpToolService mcp, TerminalUi ui, IAnsiConsole? console = null)
    {
        _store = store; _skills = skills; _mcp = mcp; _ui = ui; _native = console ?? AnsiConsole.Console;
        _display = new(_native, fullScreen: true);
        Theme = TerminalTheme.Get(store.GetSetting("terminal:theme"));
        _cancelHandler = (_, e) => { e.Cancel = true; _promptCancellation?.Cancel(); };
    }
    public void Enter()
    {
        if (Current != null) throw new InvalidOperationException("A full-screen terminal is already active.");
        Current = this;
        Console.CancelKeyPress += _cancelHandler;
        if (OperatingSystem.IsWindows()) ConsoleModes.EnableVirtualTerminal();
        Console.Write("\u001b[?1049h\u001b[?2004h\u001b[?1002h\u001b[?1006h"); _entered = true;
        _native.Clear();
        _display.Start(new Text(""));
    }
    public void Session(Guid id, string model, bool reload = false) => Session(id, model, _store.GetVisibleMessages(id), reload);

    /// <summary>Rebuilds the transcript from an explicit history. Re-attaching to a run uses the run's
    /// own journal history: the persisted conversation may already contain iterations the live tail replays.</summary>
    public void Session(Guid id, string model, IReadOnlyList<AllAgent.NET.Core.Models.Message> history, bool reload)
    {
        Model = model;
        if (SessionId == id && !reload) return;
        _ui.RestoreContextUsage(_store.GetConversation(id));
        _cachedSidebar = null;
        SessionId = id; _scroll = _lastRows = _lastTranscriptWidth = 0; _conversationPrompt = null; _transcriptSelectionStart = _transcriptSelectionEnd = null; _dragSelecting = _dragSelectingTranscript = false; _selectionNotice = null; _transcript.Clear(); _executions.Clear(); _traceCells.Clear(); CloseInspector();
        _messageAnchors.Clear(); _jumpTarget = null;
        var blocks = new Dictionary<string, CodeExecutionBlock>();
        foreach (var message in history)
        {
            if (!message.IsInternal && message.Role is "user" or "assistant" && (!string.IsNullOrEmpty(message.Content) || message.Images.Count > 0))
            {
                var block = Message(message.Role == "user" ? L.Pick("你", "You") : "AllAgent", message.Role == "user" ? SessionTimeline.Text(message) : message.Content, message.Role == "user");
                _messageAnchors[message.Id] = block; _transcript.Add(block);
            }
            if (!message.IsInternal && message.Images.Count > 0)
                _transcript.Add(ImageAttachmentsView.Render(message.Images));
            if (!message.IsInternal && _ui.ShowThinking && message.ReasoningContent is { Length: > 0 } thought)
            {
                var thinking = new CodeExecutionBlock("thought-" + message.Id, thought, reasoning: true);
                RegisterExecution(thinking); _transcript.Add(thinking);
            }
            try
            {
                if (message.IsHostActivity && message.HostToolTraceJson != null)
                    foreach (var trace in JsonSerializer.Deserialize<HostToolTrace[]>(message.HostToolTraceJson, AllAgent.NET.Core.Tools.ToolResult.JsonOptions) ?? [])
                    {
                        var block = new CodeExecutionBlock("host-" + trace.Id, "");
                        block.Start(new(trace.Id, trace.ToolName, trace.ArgsJson));
                        block.Complete(new(trace.Id, trace.ToolName, trace.ResultJson));
                        block.Complete(new(block.Id, "execute_code", trace.ResultJson));
                        RegisterExecution(block); _transcript.Add(block);
                    }
                if (message.Role == "assistant" && message.ToolCallsJson != null)
                {
                    using var calls = JsonDocument.Parse(message.ToolCallsJson);
                    foreach (var call in calls.RootElement.EnumerateArray())
                    {
                        var function = call.GetProperty("function"); if (function.GetProperty("name").GetString() != "execute_code") continue;
                        using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                        var block = new CodeExecutionBlock(call.GetProperty("id").GetString()!, arguments.RootElement.GetProperty("code").GetString()!);
                        blocks[block.Id] = block; RegisterExecution(block); _transcript.Add(block);
                    }
                }
                if (message.Role == "tool" && message.ToolCallId != null && blocks.TryGetValue(message.ToolCallId, out var completed))
                {
                    completed.Complete(new(message.ToolCallId, "execute_code", message.Content));
                    if (message.HostToolTraceJson != null)
                        foreach (var trace in JsonSerializer.Deserialize<HostToolTrace[]>(message.HostToolTraceJson, AllAgent.NET.Core.Tools.ToolResult.JsonOptions) ?? [])
                        { completed.Start(new(trace.Id, trace.ToolName, trace.ArgsJson)); completed.Complete(new(trace.Id, trace.ToolName, trace.ResultJson)); }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { /* Older or interrupted traces must not prevent restoring chat. */ }
        }
    }
    private IRenderable Message(string label, string content, bool user) => TerminalMarkdown.Message(label, content, user);
    public void UserMessage(string message) => Append(Message(L.Pick("你", "You"), message, true));
    public void Append(IRenderable renderable)
    {
        _transcript.Add(renderable); if (_transcript.Count > 800) _transcript.RemoveRange(0, 100);
        // Do not jump to the bottom while the user is reading earlier messages.
    }
    public void RegisterExecution(CodeExecutionBlock block)
    {
        block.UseMarkers = true;
        block.TaskLookup = TaskLookup;
        block.InvocationTasks = InvocationTasks;
        block.TasksRevision = TaskRevision;
        block.TaskTranscript = TaskTranscript;
        block.TaskPreview = TaskPreview;
        _executions[block.TraceId] = block;
        if (_executions.Count > 300)
        {
            var oldest = _executions.Keys.First();
            _executions[oldest].UseMarkers = false;
            _executions.Remove(oldest);
        }
    }
    internal bool InspectLatest()
    {
        if (_executions.Values.LastOrDefault(b => !b.IsReasoning && !b.IsChildTranscript) is not { } block) return false;
        OpenInspector(block, -1); return true;
    }
    internal bool InspectLatestThought()
    {
        if (_executions.Values.LastOrDefault(b => b.IsReasoning) is not { } block) return false;
        OpenInspector(block, -1); return true;
    }
    internal void InspectThought(string text) => OpenInspector(new CodeExecutionBlock("live-thought", text, reasoning: true), -1);
    private void OpenInspector(CodeExecutionBlock block, int span, bool nested = false)
    {
        ClearDetailSelection();
        if (nested && _inspector is { } current) _inspectorHistory.Push((current, _inspectorScroll, _inspectorSelection));
        else _inspectorHistory.Clear();
        var target = block.InspectionTarget(span); _inspector = (block, target.Span); _inspectorScroll = 0; _inspectorSelection = target.Selected;
    }
    private void CloseInspector() { ClearDetailSelection(); _inspectorViewport = null; _inspectorCacheKey = null; _inspector = null; _inspectorScroll = _inspectorSelection = 0; _inspectorHistory.Clear(); }
    private void BackInspector()
    {
        ClearDetailSelection();
        if (_inspectorHistory.TryPop(out var previous)) { _inspector = previous.Inspector; _inspectorScroll = previous.Scroll; _inspectorSelection = previous.Selected; }
        else CloseInspector();
    }
    private bool InspectPointer(TerminalInput input)
    {
        if (input.Pointer is not { Action: PointerAction.Down, Button: PointerButton.Left } pointer || !_traceCells.TryGetValue((pointer.X, pointer.Y), out var trace)) return false;
        var target = trace.Block.InspectionTarget(trace.Span);
        if (_inspector is { } current && current.Block == trace.Block && current.Span == target.Span && _inspectorSelection == target.Selected) return true;
        OpenInspector(trace.Block, trace.Span, nested: _inspector != null); return true;
    }
    internal bool InspectorInput(TerminalInput input)
    {
        if (_inspector == null) return false;
        if (input.Key.Key == ConsoleKey.C && input.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) return false;
        if (input.Key.Key is ConsoleKey.Escape or ConsoleKey.Enter) { BackInspector(); return true; }
        if (input.Pointer is { Button: PointerButton.Right }) return false;
        if (DetailPointer(input)) return true;
        if (input.Key.Key == ConsoleKey.A && input.Key.Modifiers.HasFlag(ConsoleModifiers.Control) && _inspectorViewport is { } all)
        { _detailStart = (0, 0); _detailEnd = (all.Width - 1, all.Lines.Count - 1); return true; }
        if (input.Wheel != 0) _inspectorScroll = Math.Max(0, _inspectorScroll - input.Wheel);
        else if (input.Key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp) _inspectorScroll = Math.Max(0, _inspectorScroll - (input.Key.Key == ConsoleKey.PageUp ? 8 : 1));
        else if (input.Key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown) _inspectorScroll += input.Key.Key == ConsoleKey.PageDown ? 8 : 1;
        else if (input.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            ClearDetailSelection();
            var trace = _inspector.Value;
            var count = trace.Block.DetailCount(trace.Span);
            _inspectorSelection = Math.Clamp(_inspectorSelection + (input.Key.Key == ConsoleKey.LeftArrow ? -1 : 1), 0, Math.Max(0, count - 1)); _inspectorScroll = 0;
        }
        return true;
    }
    public void SetActivity(IRenderable? activity) => _activity = activity;
    public void SetDialog(IRenderable? dialog) => _dialog = dialog;
    public void SetConversationPrompt(IRenderable? prompt)
    {
        if (_conversationPrompt == null && prompt != null) ScrollToLatest();
        _conversationPrompt = prompt;
    }
    public void SetInput(InputState input, IReadOnlyList<CommandHint> hints, string status) { _input = input; _hints = hints; _status = status; }
    public void ScrollToLatest() { _scroll = 0; ClearTranscriptSelection(); }
    internal void JumpToMessage(Guid message)
    {
        if (!_messageAnchors.TryGetValue(message, out var target) || !_transcript.Contains(target))
        {
            Session(SessionId, Model, reload: true);
            if (!_messageAnchors.TryGetValue(message, out target)) throw new ArgumentException(L.Pick("无法定位这条消息。", "Could not locate that message."));
        }
        ClearTranscriptSelection(); _jumpTarget = target; _lastFrameKey = null;
    }
    private void ClearTranscriptSelection() { _transcriptSelectionStart = _transcriptSelectionEnd = null; _dragSelectingTranscript = false; _selectionRegion = null; }
    public bool Navigation(ConsoleKeyInfo key)
    {
        if (_outputFocused && key.Modifiers == 0 && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        { Scroll(key.Key == ConsoleKey.UpArrow ? 1 : -1); return true; }
        if (key.KeyChar >= ' ') _outputFocused = false;
        if (key.Key == ConsoleKey.PageUp) { Scroll(Math.Max(5, _native.Profile.Height / 2)); return true; }
        if (key.Key == ConsoleKey.PageDown) { Scroll(-Math.Max(5, _native.Profile.Height / 2)); return true; }
        if (key.Key == ConsoleKey.End && key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Shift) && _input.Text.Length == 0) { _scroll = 0; return true; }
        if (key.Key == ConsoleKey.B && key.Modifiers.HasFlag(ConsoleModifiers.Control)) { ShowSidebar = !ShowSidebar; return true; }
        return false;
    }
    public bool Navigation(TerminalInput input)
    {
        if (input.Wheel != 0)
        {
            if (_dialog == null && input.MouseX is { } x && input.MouseY is { } y && x < _transcriptRight && y >= _hintsTop && y < _hintsTop + _hintsHeight && _hints.Count > 0)
                _input.MoveSuggestion(-input.Wheel, _hints.Count);
            else if (_dialog == null && (input.MouseX == null || IsTranscriptPoint(input.MouseX.Value, input.MouseY ?? -1))) Scroll(input.Wheel);
            return true;
        }
        return Navigation(input.Key);
    }
    private void Scroll(int delta)
    {
        _scroll = Math.Clamp(_scroll + delta, 0, Math.Max(0, _lastRows - _transcriptHeight));
        ClearTranscriptSelection();
    }
    internal bool SelectInput(TerminalInput input, InputState state)
    {
        if (input.Pointer is not { } pointer) return false;
        if (pointer.Button != PointerButton.Left) return true;
        if (_dialog == null && pointer.Action == PointerAction.Down && pointer.X < _transcriptRight && pointer.Y >= _hintsTop && pointer.Y < _hintsTop + _hintsHeight && _hints.Count > 0)
        {
            _outputFocused = false;
            var index = _hintStart + pointer.Y - _hintsTop;
            if (index < _hints.Count) { ClearTranscriptSelection(); _dragSelecting = false; state.Complete(_hints[index]); }
            return true;
        }
        if (_dialog == null && InspectPointer(input)) return true;
        if (_inspector != null || _conversationPrompt != null) return true;
        if (_dialog != null || _inputText == null) return true;
        var inside = pointer.X >= _inputX && pointer.X < _inputX + _inputWidth && pointer.Y >= _inputY && pointer.Y < _inputY + _inputText.VisibleRows;
        if (pointer.Action == PointerAction.Down)
        {
            _outputFocused = !inside && IsTranscriptPoint(pointer.X, pointer.Y);
            if (!inside) state.MoveTo(state.Cursor);
            _dragSelecting = inside;
            _selectionRegion = inside ? null : FindSelectionRegion(pointer.X, pointer.Y);
            _dragSelectingTranscript = _selectionRegion != null;
            if (inside) { _transcriptSelectionStart = _transcriptSelectionEnd = null; _selectionNotice = null; state.MoveTo(_inputText.HitTest(pointer.X - _inputX, pointer.Y - _inputY), pointer.Extend); }
            else if (_dragSelectingTranscript) { _selectionNotice = null; _transcriptSelectionStart = _transcriptSelectionEnd = _selectionRegion!.Value.Clamp(pointer.X, pointer.Y); }
            else { _transcriptSelectionStart = _transcriptSelectionEnd = null; }
        }
        else if (_dragSelecting)
        {
            state.MoveTo(_inputText.HitTest(pointer.X - _inputX, pointer.Y - _inputY), extend: true);
            if (pointer.Action == PointerAction.Up) _dragSelecting = false;
        }
        else if (_dragSelectingTranscript)
        {
            _transcriptSelectionEnd = _selectionRegion!.Value.Clamp(pointer.X, pointer.Y);
            if (pointer.Action == PointerAction.Up) _dragSelectingTranscript = false;
        }
        return true;
    }
    private bool IsInputPoint(int x, int y) => _inputText != null && x >= _inputX && x < _inputX + _inputWidth && y >= _inputY && y < _inputY + _inputText.VisibleRows;
    internal async Task<bool> ContextClickAsync(TerminalInput input, InputState state, Func<string, Task> paste)
    {
        if (input.Pointer is not { Button: PointerButton.Right } pointer) return false;
        if (pointer.Action != PointerAction.Down) return true;
        if (_inspector != null && _detailStart != null)
        { CopyTranscriptSelection(new('\0', ConsoleKey.C, false, false, true)); return true; }
        if (state.HasSelection) { state.CopySelection(); return true; }
        if (_transcriptSelectionStart != _transcriptSelectionEnd && _transcriptSelectionStart != null)
        { CopyTranscriptSelection(new('\0', ConsoleKey.C, false, false, true)); return true; }
        if (_dialog != null || _inspector != null || _conversationPrompt != null || !IsInputPoint(pointer.X, pointer.Y)) return true;
        var reading = Task.Run(_keys.ReadClipboard);
        while (!reading.IsCompleted) { Draw(); await Task.WhenAny(reading, Task.Delay(8)); }
        var clipboard = await reading;
        if (clipboard != null) await paste(clipboard);
        else state.InsertPaste("", L.Pick("剪贴板没有可粘贴的文字或图片", "The clipboard has no text or image to paste"));
        return true;
    }
    private SelectionRegion? FindSelectionRegion(int x, int y)
    {
        if (_selectionLines == null || y < 0 || y >= _selectionLines.Count) return null;
        SelectionRegion? selected = _visibleBlocks.FirstOrDefault(block => block.Contains(x, y));
        if (selected.Value.Right == 0)
        {
            if (x < _transcriptRight || y < 2 || y >= _lastHeight - 2) return null;
            selected = new SelectionRegion(_transcriptRight + 1, 2, _lastWidth - 1, _lastHeight - 2);
        }
        var block = selected.Value;
        string Row(int row) => string.Concat(_selectionLines[row].Select(segment => segment.Text));
        // Keep table drags in the column where the gesture started. Work in
        // rendered cells so wide Unicode and styled segments preserve hit tests.
        var separators = new List<int>();
        var column = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(Row(y));
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "│" or "┃" or "║" && column >= block.Left && column < block.Right) separators.Add(column);
            column += new Segment(element).CellCount();
        }
        if (separators.Count >= 3)
        {
            var left = separators.LastOrDefault(position => position < x, -1);
            var right = separators.FirstOrDefault(position => position > x, -1);
            if (left >= 0 && right > left) block = block with { Left = left + 1, Right = right };
        }
        if (Row(block.Top).Any(c => c is '┏' or '┌' or '╭' or '╔')) block = block with { Top = Math.Min(block.Top + 1, block.Bottom - 1) };
        if (Row(block.Bottom - 1).Any(c => c is '┗' or '└' or '╰' or '╚')) block = block with { Bottom = Math.Max(block.Top + 1, block.Bottom - 1) };
        return block;
    }

    private bool IsTranscriptPoint(int x, int y) => x >= _transcriptLeft && x < _transcriptRight && y >= _transcriptTop && y < _transcriptTop + _transcriptHeight;
    private (int X, int Y) ClampTranscriptPoint(int x, int y) => (Math.Clamp(x, _transcriptLeft, Math.Max(_transcriptLeft, _transcriptRight - 1)), Math.Clamp(y, _transcriptTop, Math.Max(_transcriptTop, _transcriptTop + _transcriptHeight - 1)));
    public bool CopyTranscriptSelection(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control) && _inspectorViewport is { } detail && _detailStart is { } a && _detailEnd is { } b)
        {
            var text = new SelectionRegion(0, 0, detail.Width, detail.Lines.Count).Extract(detail.Lines, a, b);
            _selectionNotice = ClipboardText.Write(text) ? L.Pick("已复制选中详情文字", "Copied the selected detail text") : L.Pick("剪贴板暂不可用", "Clipboard unavailable");
            return true;
        }
        if (key.Key != ConsoleKey.C || !key.Modifiers.HasFlag(ConsoleModifiers.Control) || _transcriptSelectionStart is not { } start || _transcriptSelectionEnd is not { } end) return false;
        var selected = ExtractSelection(start, end);
        if (selected.Length == 0) { _selectionNotice = L.Pick("请先拖选消息文字", "Drag to select message text first"); return true; }
        _selectionNotice = ClipboardText.Write(selected) ? L.Pick("已复制选中消息文字", "Copied the selected message text") : L.Pick("剪贴板暂不可用", "Clipboard unavailable");
        return true;
    }
    internal string ExtractSelection((int X, int Y) first, (int X, int Y) last)
    {
        if (_selectionLines != null && _selectionRegion is { } region) return region.Extract(_selectionLines, first, last);
        return "";
    }
    internal IRenderable BuildFrame(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width < 40 || height < 16)
        {
            _inputText = null;
            return new ScreenSurface(new Rows(new Text("AllAgent", new Style(Accent, decoration: Decoration.Bold)), new Text(L.Pick("请放大终端窗口至至少 40 列 × 16 行。", "Enlarge the terminal to at least 40 columns x 16 rows."), new Style(Color.Grey)), new Text(L.Pick("输入和任务会保留；Esc 返回，Ctrl+C 取消。", "Input and tasks are preserved; Esc goes back, Ctrl+C cancels."), new Style(Color.Grey))), width, height, Background, Theme.Foreground);
        }
        var narrow = width < 100 || !ShowSidebar; var sidebarWidth = narrow ? 0 : 29;
        var inputRows = new InputLayout(_input.Text, Math.Max(1, width - sidebarWidth - 4)).Rows.Count;
        // Compact input leaves room for history above an inline approval in short windows.
        var minimumInputHeight = _conversationPrompt != null && height <= 24 ? 3 : 5;
        var inputHeight = Math.Clamp(inputRows + 2, minimumInputHeight, Math.Min(9, Math.Max(5, height / 3)));
        var hintsHeight = _hints.Count > 0 ? Math.Min(6, _hints.Count + 1) : 0;
        if (height < 24) hintsHeight = Math.Min(3, hintsHeight);
        var bodyHeight = Math.Max(1, height - 3 - inputHeight - hintsHeight - 2);
        _hintsTop = height - 3 - inputHeight - hintsHeight; _hintsHeight = hintsHeight == 0 ? 0 : hintsHeight - 1;
        _transcriptTop = 3; _transcriptHeight = Math.Max(1, bodyHeight - 2); _transcriptLeft = 1; _transcriptRight = Math.Max(2, width - sidebarWidth - 1);
        var conversation = _store.GetConversation(SessionId);
        var title = conversation?.Title is { Length: > 0 } text && text != "New Conversation" ? text : L.Pick("开始新的对话", "Start a new conversation");
        var layout = new Layout("screen").SplitRows(new Layout("header").Size(2), new Layout("main"), new Layout("footer").Size(3));
        layout["header"].Update(new Rows(new SingleLineText(" AllAgent │ " + title, new Style(Accent, decoration: Decoration.Bold)), new SingleLineText(" " + _ui.Workspace, new Style(Color.Grey))));
        var dialog = _dialog;
        if (dialog == null && _inspector is { } inspection)
        {
            var task = inspection.Block.SelectedTask(inspection.Span, _inspectorSelection);
            var child = task != null && (task.Kind == "agent" || task.Kind != "shell" && task.ParentSessionId != null);
            var cacheKey = (inspection.Block, inspection.Span, _inspectorSelection, inspection.Block.Version);
            if (_inspectorCacheKey != cacheKey)
            {
                _inspectorViewport = new TextViewport(inspection.Block.Details(inspection.Span, _inspectorSelection), Math.Max(2, height - 10), _inspectorScroll);
                _inspectorCacheKey = cacheKey;
            }
            var viewport = _inspectorViewport!;
            viewport.Height = Math.Max(2, height - 10); viewport.Offset = _inspectorScroll;
            viewport.SelectionStart = _detailStart; viewport.SelectionEnd = _detailEnd;
            viewport.SelectionColor = Accent;
            _detailLeft = 2; _detailTop = 5;
            dialog = new Panel(viewport).Header(child ? L.Pick("子代理消息 · ", "Child-agent message · ") + Markup.Escape(TerminalSafeText.Clean(task!.Title)) : L.Pick("工具执行详情", "Tool execution details")).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1);
        }
        if (dialog != null)
        {
            layout["main"].Update(new Align(dialog, HorizontalAlignment.Center, VerticalAlignment.Middle));
            layout["footer"].Update(new Text(_busyDialogActive ? L.Pick(" 正在自动连接 · 双击 Esc / Ctrl+C 取消", " Connecting automatically · double Esc / Ctrl+C to cancel") : _inspector != null && _dialog == null ? L.Pick(" 滚轮/↑↓ 查看 · 拖选/Ctrl+A 全选 · Ctrl+C 复制 · ←→ 切换 · Esc 关闭", " Wheel/↑↓ scroll · drag/Ctrl+A select · Ctrl+C copy · ←→ switch · Esc close") : L.Pick(" ↑↓ 选择 · Enter 确认 · Esc 返回 / 拒绝", " ↑↓ select · Enter confirm · Esc back / reject"), new Style(Color.Grey)));
            return new ScreenSurface(layout, width, height, Background, Theme.Foreground);
        }
        var left = new Layout("chat").SplitRows(new Layout("messages"), new Layout("suggestions").Size(Math.Max(1, hintsHeight)), new Layout("input").Size(inputHeight));
        if (hintsHeight == 0) left["suggestions"].Invisible();
        if (narrow) layout["main"].SplitColumns(left); else layout["main"].SplitColumns(left, new Layout("sidebar").Size(sidebarWidth));
        var messageRows = new List<IRenderable>(_transcript);
        if (_activity != null) messageRows.Add(_activity);
        if (_conversationPrompt != null) messageRows.Add(_conversationPrompt);
        IRenderable main;
        if (_dialog != null) main = new Align(_dialog, HorizontalAlignment.Center, VerticalAlignment.Middle);
        else if (messageRows.Count == 0) main = new Align(new Rows(new FigletText("AllAgent").Color(Accent), new Text(L.Pick("直接描述任务，Agent 会使用工具并按需派发子代理。", "Describe a task; the agent uses tools and dispatches child agents when useful."), new Style(Color.Grey)), new Text(L.Pick("\nF2 选择模型   输入 / 查看常用操作\nCtrl+J 换行   @ 引用文件   Esc 停止任务   Ctrl+C 两次退出", "\nF2 select model   type / for common actions\nCtrl+J newline   @ reference files   Esc stop task   Ctrl+C twice to exit"), new Style(Color.Grey)), new Text(L.Pick("\n第一次使用：F2 配置模型，或 /login 登录。", "\nFirst time: F2 configures a model, or use /login."), new Style(Accent))), HorizontalAlignment.Center, VerticalAlignment.Middle);
        else main = new TranscriptViewport(messageRows, Math.Max(1, bodyHeight - 2), _scroll, value => { _scroll = value; _jumpTarget = null; }, PreserveScroll, _jumpTarget,
            (top, bottom) => _visibleBlocks.Add(new(_transcriptLeft, _transcriptTop + top, _transcriptRight, _transcriptTop + bottom)));
        left["messages"].Update(new Padder(main, new Padding(1, 1)));
        if (_hints.Count > 0)
        {
            var hints = new List<IRenderable>();
            var selected = Math.Clamp(_input.Selection, 0, _hints.Count - 1);
            _hintStart = Math.Max(0, selected - 3);
            foreach (var (hint, index) in _hints.Select((h, i) => (h, i)).Skip(Math.Max(0, selected - 3)).Take(Math.Max(1, hintsHeight - 1)))
                hints.Add(new Markup(hint.MarkerMarkup + (index == selected ? "[black on " + TerminalUi.AccentMarkup() + "] › " : "[dim]   ") + Markup.Escape(hint.Text + "  " + hint.Description) + "[/]"));
            left["suggestions"].Update(new Rows(hints));
        }
        _inputText = new InputText(_input, maxRows: inputHeight - 2); _inputX = 2; _inputY = height - 3 - inputHeight + 1; _inputWidth = width - sidebarWidth - 4;
        left["input"].Update(new Panel(_inputText).Header("[bold " + TerminalUi.AccentMarkup() + "] " + (string.IsNullOrEmpty(_input.Text) ? L.Pick("输入任务", "Describe a task") : L.Pick("正在输入", "Typing")) + " [/]").Border(BoxBorder.Heavy).BorderColor(Accent).Padding(1, 0).Expand());
        if (!narrow) layout["sidebar"].Update(new ScreenSurface(new Panel(Sidebar()).Border(BoxBorder.None).Padding(1, 1).Expand(), sidebarWidth, height - 5, Surface));
        var imageLabel = PromptReferences.ImageDraftLabel(_input.Text);
        if (conversation?.Revert != null) imageLabel = L.Pick("已暂存撤回 · /redo 恢复原历史和文件 · 提交新任务后丢弃被撤回的历史", "Undo staged · /redo restores history and files · sending a new task discards the undone history") + (imageLabel.Length > 0 ? " · " + imageLabel : "");
        layout["footer"].Update(new Rows(new SingleLineText(" " + _ui.TurnTokenLabel + (ModelFooter?.Invoke() is { Length: > 0 } extra ? " │ " + extra : ""), new Style(Color.Grey)), new SingleLineText(" " + _ui.ContextLabel + " │ " + TaskNotice?.Invoke() + (_scroll > 0 ? "阅读历史 ↑ · PgDn | " : "") + (_selectionNotice ?? _input.Notice ?? _status), new Style(_ui.ContextUsedTokens >= _ui.ContextCapacityTokens * 0.85 ? Color.Yellow : Color.Grey)), new SingleLineText(" " + _ui.ThinkingEffortLabel + " │ " + (imageLabel.Length > 0 ? imageLabel : L.Pick("F1 菜单  F2 模型  Ctrl+O 展开详情  消息区拖选文字 · Ctrl+C 复制 | 输入区 Shift+方向选择", "F1 menu  F2 model  Ctrl+O details  drag to select text · Ctrl+C copy | Shift+arrows in the input")), new Style(Color.Grey))));
        return new ScreenSurface(layout, width, height, Background, Theme.Foreground);
    }
    private IRenderable Sidebar()
    {
        var now = Environment.TickCount64;
        var runRevision = RunRevision?.Invoke() ?? 0;
        var todoRevision = _store.TodosRevision;
        var planRevision = _store.PlansRevision;
        if (_cachedSidebar != null && now - _sidebarTime < 1000 && runRevision == _sidebarRunRevision && todoRevision == _sidebarTodoRevision && planRevision == _sidebarPlanRevision) return _cachedSidebar;
        _sidebarTime = now; _sidebarRunRevision = runRevision; _sidebarTodoRevision = todoRevision; _sidebarPlanRevision = planRevision;
        var sessions = SessionOperations.MainSessions(_store, _ui.Workspace).Take(5).ToArray();
        var c = _store.GetConversation(SessionId);
        var messages = _store.GetMessages(SessionId); var characters = messages.Sum(m => m.Content.Length + (m.ToolCallsJson?.Length ?? 0));
        var cacheRate = c is { CacheUsageRequests: > 0, CacheInputTokens: > 0 }
            ? $"{Math.Min(c.CachedInputTokens, c.CacheInputTokens) * 100.0 / c.CacheInputTokens:F1}%"
            : L.Pick("暂无数据", "no data");
        var cacheLabel = c.CacheUsageRequests > 0
            ? L.Pick("模型缓存命中率 · ", "Model cache hit rate · ") + cacheRate + L.Pick("（写入 ", " (write ") + c.CacheWriteInputTokens.ToString("N0", L.Culture) + " tok)"
            : L.Pick("模型缓存命中率 · ", "Model cache hit rate · ") + cacheRate;
        var rows = new List<IRenderable> { new Markup(L.Pick("[bold]会话信息[/]", "[bold]Conversation[/]")), new Text(Model, new Style(Accent)).Fold(), new Text(cacheLabel, new Style(Color.Grey)), new Text(L.Pick($"{messages.Count} 条消息 · {characters:N0} 字符\n上下文为字符估算", $"{messages.Count} messages · {characters.ToString("N0", L.Culture)} characters\ncontext is estimated by characters"), new Style(Color.Grey)), new Rule().RuleStyle("grey"), new Markup(L.Pick("[bold]最近会话 [dim]F3[/][/]", "[bold]Recent conversations [dim]F3[/][/]")) };
        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            var marker = SessionOperations.RunLabel(Runs, session.Id);
            rows.Add(new SingleLineText((session.Id == SessionId ? "› " : "  ") + (index + 1) + ". " + SessionOperations.SingleLineTitle(session.Title) + marker, new Style(session.Id == SessionId ? Accent : marker.Length > 0 ? Color.Yellow : Color.Grey)));
        }
        rows.Add(new Rule().RuleStyle("grey")); rows.Add(new Markup(L.Pick("[bold]扩展[/]", "[bold]Extensions[/]")));
        if (DateTime.UtcNow - _skillsChecked > TimeSpan.FromSeconds(1)) { _enabledSkills = _skills.CachedList.Count(s => s.Enabled); _skillsChecked = DateTime.UtcNow; }
        rows.Add(new Text(L.Pick($"Skills · {_enabledSkills} 个启用  [F4]", $"Skills · {_enabledSkills} enabled  [F4]"), new Style(Color.Grey)));
        rows.Add(new Text(L.Pick($"MCP · {_mcp.GetAllServerTools().Count} 个连接  [F5]", $"MCP · {_mcp.GetAllServerTools().Count} connected  [F5]"), new Style(Color.Grey)));
        if (ExtensionSummary != null) rows.Add(new Text(ExtensionSummary(), new Style(Accent)));
        foreach (var server in _store.McpServerConfigs.Take(4)) rows.Add(new Text(($"{(_mcp.IsConnected(server.Id) ? "●" : "○")} {server.Name}"), new Style(_mcp.IsConnected(server.Id) ? Color.Green : Color.Grey)).Crop());
        rows.Add(new Rule().RuleStyle("grey")); rows.Add(new Markup(L.Pick("[bold]操作[/]", "[bold]Actions[/]")));
        rows.Add(new Text(L.Pick("F1  打开操作菜单\nF2  切换模型 / 提供商\nF3  恢复会话\nF6  思考强度\nShift+Tab 计划模式\nCtrl+B 隐藏侧栏\nCtrl+P 搜索所有命令\n/exit 退出", "F1  action menu\nF2  switch model / provider\nF3  resume conversation\nF6  reasoning effort\nShift+Tab plan mode\nCtrl+B hide sidebar\nCtrl+P search all commands\n/exit quit"), new Style(Color.Grey)));
        // Bottom-right: the sessions that are still working while this one is on screen.
        rows.Add(new Rule().RuleStyle("grey")); rows.Add(new Markup(L.Pick("[bold]后台会话[/]", "[bold]Background conversations[/]")));
        var elsewhere = (OtherWorkspaces?.Invoke() ?? []).Where(item => item.Running > 0).ToArray();
        var background = (Runs?.Active ?? []).Where(run => run.ConversationId != SessionId).ToArray();
        if (background.Length == 0 && elsewhere.Length == 0) rows.Add(new Text(L.Pick("没有其他会话在运行\n/new 开新会话 · 切走不会停止任务", "No other conversations are running\n/new starts one · switching away does not stop tasks"), new Style(Color.Grey)));
        foreach (var workspace in elsewhere)
            rows.Add(new SingleLineText("◇ " + workspace.Name + L.Pick(" · " + workspace.Running + " 个会话运行中", " · " + workspace.Running + " conversations running") + (workspace.Waiting > 0 ? L.Pick(" · 待处理 " + workspace.Waiting, " · " + workspace.Waiting + " waiting") : "") + L.Pick("  /workspace 路径", "  /workspace path"), new Style(Color.Cyan1)));
        foreach (var run in background.Take(6))
        {
            var status = run.State switch
            {
                SessionRunState.AwaitingApproval => L.Pick("需要授权 · 切回处理", "approval needed · switch back"),
                SessionRunState.AwaitingAnswer => L.Pick("需要回答 · 切回处理", "answer needed · switch back"),
                SessionRunState.AwaitingPlan => L.Pick("等待计划审批 · 切回处理", "plan approval pending · switch back"),
                SessionRunState.Cancelling => L.Pick("正在停止", "stopping"),
                _ => L.Pick("运行中", "running"),
            };
            rows.Add(new SingleLineText("● " + SessionOperations.SingleLineTitle(run.Title) + " · " + status, new Style(run.Waiting ? Color.Yellow : Accent)));
        }
        if (background.Length > 6) rows.Add(new Text(L.Pick("… 还有 " + (background.Length - 6) + " 个", "… " + (background.Length - 6) + " more"), new Style(Color.Grey)));
        if (c != null) AddTodoRows(rows);
        if (c != null) AddPlanRows(rows);
        return _cachedSidebar = new Rows(rows);
    }
    private void AddPlanRows(List<IRenderable> rows)
    {
        AllAgent.NET.Core.Models.PlanDocument? plan;
        try { plan = _store.GetPlan(SessionId); }
        catch (ArgumentException) { return; }
        if (plan == null) return;
        var status = plan.Status switch
        {
            "approved" => L.Pick("[green]已批准 · 完全访问[/]", "[green]Approved · full access[/]"),
            "rejected" => L.Pick("[yellow]已拒绝 · 继续规划[/]", "[yellow]Rejected · keep planning[/]"),
            _ => L.Pick("[yellow]待批准[/]", "[yellow]Pending approval[/]"),
        };
        rows.Add(new Rule().RuleStyle("grey"));
        rows.Add(new Markup("[bold]PLAN[/] " + status));
        rows.Add(new SingleLineText(plan.Title, new Style(plan.Status == "approved" ? Color.Green : Color.Grey)));
        if (plan.Feedback is { Length: > 0 } feedback) rows.Add(new SingleLineText(feedback, new Style(Color.Grey)));
    }
    private void AddTodoRows(List<IRenderable> rows)
    {
        IReadOnlyList<AllAgent.NET.Core.Models.TodoItem> todos;
        try { todos = _store.GetTodos(SessionId); }
        catch (ArgumentException) { return; }
        if (todos.Count == 0) return;
        var completed = todos.Count(todo => todo.Status == "completed");
        var percent = completed * 100 / todos.Count;
        var filled = Math.Clamp((int)Math.Round(completed * 12.0 / todos.Count, MidpointRounding.AwayFromZero), 0, 12);
        rows.Add(new Rule().RuleStyle("grey"));
        rows.Add(new Markup("[bold]TODO[/] [dim]" + completed + "/" + todos.Count + "[/]" + (completed == todos.Count ? L.Pick(" [green]✓ 全部完成[/]", " [green]✓ All done[/]") : "")));
        rows.Add(new Markup("[green]" + new string('█', filled) + "[/][grey]" + new string('░', 12 - filled) + "[/] [dim]" + percent + "%[/]"));
        // Show what is being worked on now, then what comes next; completed items only when everything is done.
        var visible = todos.Where(todo => todo.Status == "in_progress").Concat(todos.Where(todo => todo.Status == "pending")).Take(4).ToArray();
        if (visible.Length == 0) visible = todos.TakeLast(2).ToArray();
        foreach (var item in visible)
            rows.Add(new SingleLineText((item.Status switch { "in_progress" => "▶ ", "completed" => "☑ ", _ => "☐ " }) + item.Content,
                new Style(item.Status == "in_progress" ? Accent : item.Status == "completed" ? Color.Green : Color.Grey)));
        if (todos.Count > visible.Length) rows.Add(new Text(L.Pick("… 还有 " + (todos.Count - visible.Length) + " 项 · /todo", "… " + (todos.Count - visible.Length) + " more · /todo"), new Style(Color.Grey)));
    }
    internal static IRenderable Input(InputState state, bool secret = false)
    {
        return new InputText(state, secret);
    }
    public void Draw()
    {
        if (!_entered)
        {
            // No live display owner (facade/tests): write the current transient dialog so callers can observe it.
            lock (_drawGate) { if (_dialog != null) _native.Write(_dialog); }
            return;
        }
        lock (_drawGate)
        {
            var width = Math.Clamp(Console.WindowWidth, 1, 500); var height = Math.Clamp(Console.WindowHeight, 1, 200);
            var now = Environment.TickCount64;
            AutoScrollDetail(now);
            var key = new HashCode();
            key.Add(width); key.Add(height); key.Add(Theme); key.Add(Model); key.Add(SessionId); key.Add(ShowSidebar);
            key.Add(_scroll); key.Add(_status); key.Add(_selectionNotice); key.Add(_ui.ContextLabel); key.Add(_ui.ThinkingEffortLabel); key.Add(_ui.TurnTokenLabel);
            key.Add(_input.Text); key.Add(_input.Cursor); key.Add(_input.Anchor); key.Add(_input.Selection); key.Add(_input.Notice);
            key.Add(_detailStart); key.Add(_detailEnd);
            key.Add(_transcriptSelectionStart); key.Add(_transcriptSelectionEnd); key.Add(_inspector); key.Add(_inspectorScroll); key.Add(_inspectorSelection);
            key.Add(_dialog); key.Add(_conversationPrompt);
            key.Add(TaskRevision?.Invoke() ?? 0); key.Add(RunRevision?.Invoke() ?? 0); key.Add(_store.TodosRevision); key.Add(_store.PlansRevision);
            // The clock only matters while an activity line or detail view is animating.
            if (_activity != null || _inspector != null) key.Add(now / 1000);
            foreach (var hint in _hints) key.Add(hint);
            foreach (var item in _transcript) { key.Add(item); if (item is IVersionedRenderable versioned) key.Add(versioned.Version); }
            // Streaming animation is capped at 20 fps; editing and scrolling still redraw immediately.
            if (_activity != null) key.Add(now / 50);
            var frameKey = key.ToHashCode();
            if (_lastFrameKey == frameKey) return;
            _lastFrameKey = frameKey;
            if (_lastWidth > 0 && (_lastWidth != width || _lastHeight != height))
            {
                _display.Stop(); _native.Clear(); _display.Start(new Text("")); ClearTranscriptSelection();
            }
            _native.Profile.Width = width; _native.Profile.Height = height;
            var segments = RenderFrame(width, height);
            if (_lastWidth == width && _lastHeight == height && _lastSegments?.Length == segments.Length && segments.Zip(_lastSegments).All(s => s.First.Text == s.Second.Text && s.First.Style.Equals(s.Second.Style))) return;
            _lastSegments = segments; _lastWidth = width; _lastHeight = height;
            _display.Update(segments, width, height);
        }
    }
    internal Segment[] RenderFrame(int width, int height)
    {
        _visibleBlocks.Clear();
        var segments = BuildFrame(width, height).Render(RenderOptions.Create(_native, _native.Profile.Capabilities), width).ToArray();
        if (_inspectorViewport != null && _inspector != null) _inspectorScroll = _inspectorViewport.Offset;
        _traceCells.Clear();
        var markers = _executions.Values.SelectMany(block => block.Markers.Select(pair => (Color: pair.Value, Block: block, Span: pair.Key))).ToDictionary(pair => pair.Color);
        var lines = Segment.SplitLines(segments, width, height);
        _selectionLines = lines;
        for (var y = 0; y < lines.Count; y++)
        {
            var x = 0;
            foreach (var segment in lines[y])
            {
                var cells = segment.CellCount();
                if (markers.TryGetValue(segment.Style.Background, out var trace))
                {
                    for (var cell = x; cell < x + cells; cell++) _traceCells[(cell, y)] = (trace.Block, trace.Span);
                }
                x += cells;
            }
        }
        // Marker colors are internal hit-test metadata and are removed before terminal output.
        if (_transcriptSelectionStart is not { } selectionStart || _transcriptSelectionEnd is not { } selectionEnd)
            return segments.Select(segment => markers.ContainsKey(segment.Style.Background)
                ? new Segment(segment.Text, new Style(segment.Style.Foreground, Background, segment.Style.Decoration)) : segment).ToArray();
        if (selectionStart.Y > selectionEnd.Y || selectionStart.Y == selectionEnd.Y && selectionStart.X > selectionEnd.X)
            (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
        var highlighted = new List<Segment>();
        for (var y = 0; y < lines.Count; y++)
        {
            var x = 0;
            foreach (var segment in lines[y])
            {
                var style = markers.ContainsKey(segment.Style.Background)
                    ? new Style(segment.Style.Foreground, Background, segment.Style.Decoration)
                    : segment.Style;
                var segmentCells = segment.CellCount();
                if (_selectionRegion is not { } activeRegion || y < selectionStart.Y || y > selectionEnd.Y || y < activeRegion.Top || y >= activeRegion.Bottom || x >= activeRegion.Right || x + segmentCells <= activeRegion.Left)
                { highlighted.Add(new Segment(segment.Text, style)); x += segmentCells; continue; }
                var enumerator = StringInfo.GetTextElementEnumerator(segment.Text);
                while (enumerator.MoveNext())
                {
                    var element = enumerator.GetTextElement();
                    var cells = new Segment(element, style).CellCount();
                    var inSelection = cells > 0 && _selectionRegion?.Selected(x, y, cells, selectionStart, selectionEnd) == true;
                    highlighted.Add(new Segment(element, inSelection
                        ? new Style(Color.Black, Accent, style.Decoration) : style));
                    x += cells;
                }
            }
            if (y < lines.Count - 1) highlighted.Add(Segment.LineBreak);
        }
        return highlighted.ToArray();
    }
    private async Task<TerminalInput> ReadKeyAsync(TerminalKeyReader reader, CancellationToken ct)
    {
        var pending = reader.ReadAsync(ct);
        while (!pending.IsCompleted)
        {
            await Task.WhenAny(pending, Task.Delay(33, ct));
            Draw();
        }
        return await pending;
    }
    public async Task<T> BusyAsync<T>(string title, Func<CancellationToken, Task<T>> action, CancellationToken ct = default, Func<string>? titleProvider = null, bool allowEscape = false, Func<IRenderable>? bodyProvider = null)
    {
        // Actions may open prompts; use our display owner instead of nesting Spectre Status/Live scopes.
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var keys = allowEscape ? _keys : null;
        var previousDialog = _dialog;
        var previousBusyDialog = _busyDialogActive;
        if (bodyProvider != null) _busyDialogActive = true;
        try
        {
            var task = action(cancelled.Token);
            while (!task.IsCompleted)
            {
                cancelled.Token.ThrowIfCancellationRequested();
                var glyph = LoadingIndicator.Frame(watch.ElapsedMilliseconds);
                if (bodyProvider != null) SetDialog(new Panel(new Rows(bodyProvider(), new Text($"\n{glyph} {titleProvider?.Invoke() ?? title} · {watch.Elapsed.TotalSeconds:F0}s · {(allowEscape ? L.Pick("Esc 取消 · ", "Esc cancel · ") : "")}Ctrl+C" + L.Pick(" 取消", " cancel"), new Style(Accent))))
                    .Header(Markup.Escape(title)).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1));
                SetActivity(new Text($"{glyph} {titleProvider?.Invoke() ?? title}… {watch.Elapsed.TotalSeconds:F0}s · {(allowEscape ? L.Pick("Esc 取消 · ", "Esc cancel · ") : "")}Ctrl+C" + L.Pick(" 取消", " cancel"), new Style(Accent))); Draw();
                if (keys?.HasInput == true)
                {
                    var key = await keys.ReadAsync(cancelled.Token);
                    if (key.Paste == null && key.Key.Key == ConsoleKey.Escape) cancelled.Cancel();
                    else Navigation(key);
                }
                await Task.WhenAny(task, Task.Delay(33, CancellationToken.None));
            }
            return await task;
        }
        finally { if (bodyProvider != null) SetDialog(previousDialog); _busyDialogActive = previousBusyDialog; SetActivity(null); Console.CancelKeyPress -= handler; Draw(); }
    }
    public async Task<string> ReadInputAsync(InputEditor editor, string status, string initial, CancellationToken ct)
    {
        using var inputMode = new TextInputMode(); _dragSelecting = false;
        _activity = null; var state = _promptInput; if (state.Text != initial) state.Set(initial); _ui.Draft = ""; var input = _keys;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                SetInput(state, editor.Hints(state), status); Draw();
                var nextIdleRefresh = Environment.TickCount64 + 100;
                while (!input.HasInput && !cancel.IsCancellationRequested)
                {
                    if (editor.WakeRequested?.Invoke() == true) { _ui.Draft = state.Text; editor.InterruptedDraft = state.Text; return ""; }
                    await Task.Delay(8, cancel.Token);
                    if (Environment.TickCount64 >= nextIdleRefresh)
                    {
                        SetInput(state, editor.Hints(state), status); Draw();
                        nextIdleRefresh = Environment.TickCount64 + 100;
                    }
                }
                var key = await ReadKeyAsync(input, cancel.Token);
                if (InspectorInput(key)) continue;
                if (key.Key.Key == ConsoleKey.O && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    // Ctrl+O expands the newest step detail: execution first, then thinking.
                    if (!InspectLatest()) InspectLatestThought();
                    continue;
                }
                if (key.Paste != null) { await editor.PasteAsync(state, key.Paste, cancel.Token); continue; }
                if (await ContextClickAsync(key, state, text => editor.PasteAsync(state, text, cancel.Token))) continue;
                if (SelectInput(key, state)) continue;
                if (key.Key.Key == ConsoleKey.C && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control) && state.HasSelection) { state.Key(key.Key, editor.Hints(state), editor.History); continue; }
                if (CopyTranscriptSelection(key.Key)) continue;
                if (Navigation(key)) continue;
                string? shortcut = null;
                try
                {
                    shortcut = key.Key.Key switch { ConsoleKey.F1 => await MenuAsync(cancel.Token), ConsoleKey.F2 => "/model", ConsoleKey.F3 => "/session", ConsoleKey.F4 => "/skills", ConsoleKey.F5 => "/mcp", ConsoleKey.F6 => "/effort", _ => null };
                    if (key.Key.Key == ConsoleKey.P && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) shortcut = await PaletteAsync(editor, cancel.Token);
                    if (key.Key.Key == ConsoleKey.T && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) shortcut = "/effort next";
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { continue; }
                if (key.Key.Key == ConsoleKey.Tab && key.Key.Modifiers.HasFlag(ConsoleModifiers.Shift)) shortcut = "/plan";
                if (shortcut != null) { _ui.Draft = state.Text; return shortcut; }
                var submitted = state.Key(key.Key, editor.Hints(state), editor.History);
                if (submitted != null)
                {
                    if (submitted.Length > 0)
                    {
                        state.Set(""); ScrollToLatest();
                        if (!submitted.StartsWith('/')) { UserMessage(submitted); SetInput(state, [], L.Pick("任务已提交 · 准备 Agent", "Task submitted · preparing the agent")); Draw(); }
                    }
                    return submitted;
                }
            }
            return "";
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    private async Task<string?> MenuAsync(CancellationToken ct)
    {
        var entries = new[] {
            (L.Pick("继续聊天", "Continue chatting"), ""),
            (L.Pick("开始新任务", "Start a new task"), "/new"),
            (L.Pick("恢复历史会话", "Resume a conversation"), "/session"),
            (L.Pick("选择 / 配置模型", "Select / configure a model"), "/model"),
            (L.Pick("账号登录", "Account sign-in"), "/login"),
            (L.Pick("子代理与后台任务", "Child agents and background tasks"), "/tasks"),
            (L.Pick("撤销上一轮任务", "Undo the last turn"), "/undo"),
            (L.Pick("权限模式", "Permission mode"), "/permissions"),
            (L.Pick("显示与主题设置", "Display and theme settings"), "/settings"),
            (L.Pick("全部操作与快捷键", "All actions and shortcuts"), "/help all"),
        };
        var selected = await ChooseAsync(L.Pick("操作菜单", "Action menu"), entries, e => e.Item1, ct); return selected.Item2.Length > 0 ? selected.Item2 : null;
    }
    private async Task<string?> PaletteAsync(InputEditor editor, CancellationToken ct)
    {
        var choice = await ChooseAsync(L.Pick("命令面板 · 输入关键字搜索", "Command panel · type to search"), editor.Commands().ToArray(), h => h.Text + " · " + h.Description, ct); return choice.Text;
    }
    public T Choose<T>(string title, IReadOnlyList<T> choices, Func<T,string> label) => ChooseAsync(title, choices, label, CancellationToken.None).GetAwaiter().GetResult();
    internal sealed record TimelineChoice(Guid MessageId, string Action);
    private TerminalKeyReader OverlayKeys => _entered ? _keys : new TerminalKeyReader(_native.Input.IsKeyAvailable, () => _native.Input.ReadKey(true) ?? default);
    public async Task<TimelineChoice?> TimelineAsync(CancellationToken ct = default)
    {
        Session(SessionId, Model, reload: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        var previous = _dialog;
        var restoreScroll = _scroll;
        var selected = 0; var search = new InputState();
        Guid? chosen = null;
        try
        {
            while (chosen == null)
            {
                var entries = SessionTimeline.Entries(_store, SessionId).Where(message => SessionTimeline.Text(message).Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                    || message.Id.ToString().StartsWith(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
                selected = Math.Clamp(selected, 0, Math.Max(0, entries.Length - 1));
                if (entries.Length > 0) JumpToMessage(entries[selected].Id);
                var listHeight = Math.Clamp(_native.Profile.Height - 9, 3, 16);
                var start = Math.Clamp(selected - listHeight + 1, 0, Math.Max(0, entries.Length - listHeight));
                var rows = new List<IRenderable> { new Markup(L.Pick("[dim]搜索 › [/]", "[dim]Search › [/]") + Markup.Escape(search.Text) + "[dim]  · " + entries.Length + L.Pick(" 条[/]", " items[/]")) };
                foreach (var (message, index) in entries.Select((message, index) => (message, index)).Skip(start).Take(listHeight))
                {
                    var label = message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + message.Id.ToString()[..8] + " · " + SessionTimeline.Text(message).Replace('\n', ' ');
                    rows.Add(index == selected
                        ? new Markup("[black on " + TerminalUi.AccentMarkup() + "]› " + Markup.Escape(TerminalSafeText.Clean(label)) + " [/]")
                        : new Markup("  " + Markup.Escape(TerminalSafeText.Clean(label))));
                }
                if (entries.Length == 0) rows.Add(new Text(L.Pick("没有匹配的用户消息。", "No matching user messages."), new Style(Color.Grey)));
                rows.Add(new Text(L.Pick("↑↓ 选择并定位 · 输入搜索 · Enter 消息操作 · Esc 返回会话", "↑↓ select and jump · type to search · Enter message actions · Esc back"), new Style(Color.Grey)));
                SetDialog(new Panel(new Rows(rows)).Header(L.Pick("会话时间线", "Conversation timeline")).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1));
                Draw();
                var input = await ReadKeyAsync(OverlayKeys, cancellation.Token); var key = input.Key;
                if (input.Paste != null) { search.Insert(input.Paste); selected = 0; continue; }
                if (input.Wheel != 0) { selected = Math.Clamp(selected - input.Wheel, 0, Math.Max(0, entries.Length - 1)); continue; }
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) { _scroll = restoreScroll; return null; }
                if (key.Key == ConsoleKey.UpArrow) selected = Math.Max(0, selected - 1);
                else if (key.Key == ConsoleKey.DownArrow) selected = Math.Min(entries.Length - 1, selected + 1);
                else if (key.Key == ConsoleKey.Enter) { if (entries.Length > 0) chosen = entries[selected].Id; }
                else { search.Key(key, [], []); selected = 0; }
            }
        }
        finally { _promptCancellation = null; SetDialog(previous); Draw(); }
        var actions = new[] {
            (L.Pick("跳转到消息", "Jump to message"), "jump"),
            (L.Pick("复制消息正文", "Copy message text"), "copy"),
            (L.Pick("恢复到草稿", "Restore to draft"), "restore"),
            (L.Pick("从此消息之前分支", "Branch before this message"), "fork"),
            (L.Pick("撤回此消息起的任务和文件修改", "Undo tasks and file changes from this message"), "revert"),
        };
        var action = await ChooseAsync(L.Pick("消息操作", "Message actions"), actions, item => item.Item1, ct);
        return new(chosen.Value, action.Item2);
    }
    public async Task<string?> PendingInputsAsync(InputInbox inbox, string draft, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        var previous = _dialog;
        var selected = 0; string? notice = null;
        try
        {
            while (true)
            {
                var items = inbox.Pending;
                if (items.Count == 0) return null;
                selected = Math.Clamp(selected, 0, items.Count - 1);
                var listHeight = Math.Clamp(_native.Profile.Height - 9, 3, 16);
                var start = Math.Clamp(selected - listHeight + 1, 0, Math.Max(0, items.Count - listHeight));
                var rows = new List<IRenderable>();
                foreach (var (item, index) in items.Select((item, index) => (item, index)).Skip(start).Take(listHeight))
                {
                    var label = item.Id.ToString()[..8] + " · " + item.Delivery + " · " + item.Draft.Replace('\n', ' ') + (item.Images.Count > 0 ? L.Pick($" · 图片 {item.Images.Count}", $" · {item.Images.Count} images") : "");
                    rows.Add(index == selected
                        ? new Markup("[black on " + TerminalUi.AccentMarkup() + "]› " + Markup.Escape(TerminalSafeText.Clean(label)) + " [/]")
                        : new Markup("  " + Markup.Escape(TerminalSafeText.Clean(label))));
                }
                rows.Add(new Text(L.Pick("Enter 当前任务处理 · Tab 移回队列 · Ctrl+D/Delete 删除 · Ctrl+U/Z 恢复草稿 · Esc 返回", "Enter process in the current task · Tab move back to queue · Ctrl+D/Delete remove · Ctrl+U/Z restore draft · Esc back"), new Style(Color.Grey)));
                if (notice != null) rows.Add(new Text(notice, new Style(Color.Yellow)));
                SetDialog(new Panel(new Rows(rows)).Header(L.Pick("待处理输入", "Pending input")).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1));
                Draw();
                var input = await ReadKeyAsync(OverlayKeys, cancellation.Token); var key = input.Key;
                if (key.Key == ConsoleKey.Escape) return null;
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return null;
                if (key.Key == ConsoleKey.UpArrow) { selected = Math.Max(0, selected - 1); continue; }
                if (key.Key == ConsoleKey.DownArrow) { selected = Math.Min(items.Count - 1, selected + 1); continue; }
                var action = key.Key switch {
                    ConsoleKey.Enter => "steer", ConsoleKey.Tab => "queue", ConsoleKey.Delete => "delete",
                    ConsoleKey.Z or ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control) => "edit",
                    ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control) => "delete", _ => null
                };
                if (action == null) continue;
                try
                {
                    var restored = await inbox.CommandAsync("/queue " + action + " " + items[selected].Id, draft, cancellation.Token);
                    if (action is "steer" or "edit") return restored;
                    notice = action == "delete" ? L.Pick("已删除待处理输入。", "Pending input removed.") : L.Pick("已移回队列。", "Moved back to the queue.");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                { notice = ex.Message; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { _promptCancellation = null; SetDialog(previous); Draw(); }
    }
    public async Task<T> ChooseAsync<T>(string title, IReadOnlyList<T> choices, Func<T,string> label, CancellationToken ct, Func<T,string>? group = null, Func<T,string>? color = null, IRenderable? body = null)
    {
        if (choices.Count == 0) throw new ArgumentException(L.Pick("没有可选项。", "There are no options."));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        var previous = _dialog;
        var search = new InputState();
        var selected = 0;
        try
        {
            while (true)
            {
                var filtered = search.Text.Length == 0 ? choices.ToArray() : choices.Where(item => label(item).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
                selected = Math.Clamp(selected, 0, Math.Max(0, filtered.Length - 1));
                var bodyHeight = body == null ? 0 : Math.Clamp(_native.Profile.Height / 4, 3, 8);
                var listHeight = Math.Clamp(_native.Profile.Height - (body == null ? 9 : bodyHeight + 11), 3, 14);
                var start = Math.Clamp(selected - listHeight + 1, 0, Math.Max(0, filtered.Length - listHeight));
                var rows = new List<IRenderable>();
                if (body != null) rows.Add(new TextViewport(new SafeTerminalRenderable(body), bodyHeight, 0));
                if (search.Text.Length > 0) rows.Add(new Markup(L.Pick("[dim]过滤 › [/]", "[dim]Filter › [/]") + Markup.Escape(search.Text) + "[dim]  · " + filtered.Length + L.Pick(" 项[/]", " items[/]")));
                if (filtered.Length == 0) rows.Add(new Text(L.Pick("没有匹配项；退格调整过滤条件。", "No matches; press Backspace to adjust the filter."), new Style(Color.Yellow)));
                foreach (var (item, index) in filtered.Select((item, index) => (item, index)).Skip(start).Take(listHeight))
                {
                    var text = label(item) + (group?.Invoke(item) is { Length: > 0 } extra ? " · " + extra : "");
                    var styled = color?.Invoke(item) is { Length: > 0 } tint ? $"[{tint}]{Markup.Escape(text)}[/]" : Markup.Escape(text);
                    rows.Add(index == selected
                        ? new Markup("[black on " + TerminalUi.AccentMarkup() + "]› " + styled + " [/]")
                        : new Markup("  " + styled));
                }
                if (filtered.Length > listHeight) rows.Add(new Text(L.Pick($"  {start + 1}–{Math.Min(filtered.Length, start + listHeight)} / {filtered.Length} · ↑↓ 滚动", $"  {start + 1}-{Math.Min(filtered.Length, start + listHeight)} / {filtered.Length} · ↑↓ scroll"), new Style(Color.Grey)));
                rows.Add(new Text(L.Pick("↑↓ 选择 · Enter 确认 · Esc 返回 · 输入过滤", "↑↓ select · Enter confirm · Esc back · type to filter"), new Style(Color.Grey)));
                SetDialog(new Panel(new Rows(rows)).Header(Markup.Escape(TerminalSafeText.Clean(title))).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1));
                Draw();
                var input = await ReadKeyAsync(OverlayKeys, cancellation.Token); var key = input.Key;
                if (input.Paste != null) { search.Insert(input.Paste); selected = 0; continue; }
                if (input.Wheel != 0) { selected = Math.Clamp(selected - input.Wheel, 0, Math.Max(0, filtered.Length - 1)); continue; }
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException(L.Pick("已返回。", "Returned."));
                if (key.Key == ConsoleKey.Enter) { if (filtered.Length > 0) return filtered[selected]; continue; }
                if (key.Key == ConsoleKey.UpArrow) { selected = Math.Max(0, selected - 1); continue; }
                if (key.Key == ConsoleKey.DownArrow) { selected = Math.Min(Math.Max(0, filtered.Length - 1), selected + 1); continue; }
                if (key.Key == ConsoleKey.PageUp) { selected = Math.Max(0, selected - listHeight); continue; }
                if (key.Key == ConsoleKey.PageDown) { selected = Math.Min(Math.Max(0, filtered.Length - 1), selected + listHeight); continue; }
                search.Key(key, [], []);
                selected = 0;
            }
        }
        finally { _promptCancellation = null; SetDialog(previous); Draw(); }
    }
    public async Task<string?> QuestionAsync(AllAgent.NET.Core.Tools.AgentQuestion question, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        try
        {
            return await TerminalQuestionPrompt.AskAsync(_native,
                _entered ? _keys : new TerminalKeyReader(_native.Input.IsKeyAvailable, () => _native.Input.ReadKey(true) ?? default),
                question, cancellation.Token, body =>
                {
                    SetConversationPrompt(new Panel(body).Header(L.Pick("需要你的回答", "Your answer is needed")).Border(BoxBorder.Rounded).BorderColor(Color.Yellow));
                    Draw();
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { SetConversationPrompt(null); _promptCancellation = null; Draw(); }
    }

    public async Task<string> TextAsync(string title, string? defaultValue, bool secret, bool empty, CancellationToken ct = default, IRenderable? body = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        var previous = _dialog;
        var state = new InputState(); if (defaultValue is { Length: > 0 }) state.Set(defaultValue);
        try
        {
            while (true)
            {
                var rows = new List<IRenderable>();
                if (body != null) rows.Add(new TextViewport(new SafeTerminalRenderable(body), Math.Clamp(_native.Profile.Height / 4, 3, 8), 0));
                rows.Add(new InputText(state, secret, Math.Clamp(_native.Profile.Height - 10, 1, 8)));
                if (state.Notice is { Length: > 0 } notice) rows.Add(new Text(notice, new Style(Color.Yellow)));
                rows.Add(new Text(L.Pick("Enter 保存 · Esc 返回 · Ctrl+V 粘贴", "Enter save · Esc back · Ctrl+V paste"), new Style(Color.Grey)));
                SetDialog(new Panel(new Rows(rows)).Header(Markup.Escape(TerminalSafeText.Clean(title))).Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1));
                Draw();
                var input = await ReadKeyAsync(OverlayKeys, cancellation.Token); var key = input.Key;
                if (input.Paste != null) { state.Insert(input.Paste); continue; }
                if (key.Key == ConsoleKey.Escape && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)) throw new OperationCanceledException(L.Pick("已返回。", "Returned."));
                var submitted = state.Key(key, [], []);
                if (submitted == null) continue;
                if (!empty && string.IsNullOrWhiteSpace(submitted)) { state.SetNotice(L.Pick("此项不能为空。", "This field cannot be empty.")); continue; }
                return submitted;
            }
        }
        finally { _promptCancellation = null; SetDialog(previous); Draw(); }
    }
    public async Task<bool> ConfirmAsync(string title, CancellationToken ct = default)
    {
        var choice = await ChooseAsync(title, new[]
        {
            (Id: false, Label: L.Pick("返回 / 拒绝", "Back / Reject")),
            (Id: true, Label: L.Pick("确认", "Confirm")),
        }, item => item.Label, ct);
        return choice.Id;
    }
    public async Task<bool> ApproveAsync(ApprovalPreview preview, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); _promptCancellation = cancellation;
        var reader = _keys; var selected = 0; var scroll = 0; var expanded = false;
        try
        {
            while (true)
            {
                SetConversationPrompt(new Panel(preview.Render(selected, scroll, expanded, 2)).Header(L.Pick("需要授权", "Approval required")).Border(BoxBorder.Rounded).BorderColor(Color.Yellow)); Draw();
                var input = await ReadKeyAsync(reader, cancellation.Token); var key = input.Key;
                if (input.Wheel != 0) { scroll = Math.Clamp(scroll - input.Wheel, 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (input.Paste != null) continue;
                if (key.Key is ConsoleKey.Escape or ConsoleKey.N) return false;
                if (key.Key == ConsoleKey.Y) return true;
                if (key.Key is ConsoleKey.A or ConsoleKey.D3 && preview.RememberForSession != null) { preview.RememberForSession(); return true; }
                if (key.Key == ConsoleKey.Tab) { expanded = !expanded; continue; }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) { scroll = Math.Clamp(scroll + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) { var count = preview.RememberForSession == null ? 2 : 3; selected = Math.Clamp(selected + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, count - 1); }
                if (key.Key == ConsoleKey.Enter) { if (selected == 2) preview.RememberForSession?.Invoke(); return selected != 1; }
            }
        }
        finally { SetConversationPrompt(null); _promptCancellation = null; Draw(); }
    }
    public async Task<bool> ApprovePlanAsync(PlanPreview preview, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); _promptCancellation = cancellation;
        var reader = _keys; var selected = 1; var scroll = 0; var expanded = false;
        try
        {
            while (true)
            {
                SetConversationPrompt(new Panel(preview.Render(selected, scroll, expanded, 6)).Header(L.Pick("计划审批", "Plan approval")).Border(BoxBorder.Rounded).BorderColor(Color.Yellow)); Draw();
                var input = await ReadKeyAsync(reader, cancellation.Token); var key = input.Key;
                if (input.Wheel != 0) { scroll = Math.Clamp(scroll - input.Wheel, 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (input.Paste != null) continue;
                if (key.Key is ConsoleKey.Escape or ConsoleKey.N or ConsoleKey.D2 or ConsoleKey.NumPad2) return false;
                if (key.Key is ConsoleKey.Y or ConsoleKey.D1 or ConsoleKey.NumPad1) return true;
                if (key.Key == ConsoleKey.Tab) { expanded = !expanded; continue; }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) { scroll = Math.Clamp(scroll + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) selected = Math.Clamp(selected + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, 1);
                if (key.Key == ConsoleKey.Enter) return selected == 0;
            }
        }
        finally { SetConversationPrompt(null); _promptCancellation = null; Draw(); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (Current == this) Current = null;
        Console.CancelKeyPress -= _cancelHandler;
        try { _display.Dispose(); }
        finally { if (_entered) { Console.Write("\u001b[?1006l\u001b[?1002l\u001b[?1000l\u001b[?2004l\u001b[0m\u001b[?25h\u001b[?1049l"); ConsoleModes.Restore(); } }
    }
}

internal sealed class SegmentFrame(IReadOnlyList<Segment> segments, int width) : IRenderable
{
    private readonly IReadOnlyList<Segment> _output = Compact(segments);
    public Measurement Measure(RenderOptions options, int maxWidth) => new(width, width);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => _output;
    private static IReadOnlyList<Segment> Compact(IReadOnlyList<Segment> source)
    {
        // Hit testing is already complete. Merge equal output styles so borders and
        // selected text do not generate an ANSI color reset for every character.
        var result = new List<Segment>(); var text = new StringBuilder(); Style? style = null;
        void Flush() { if (text.Length == 0) return; result.Add(new(text.ToString(), style!.Value)); text.Clear(); }
        foreach (var segment in source)
        {
            if (segment.IsControlCode || segment.Text.Contains('\n') || segment.Text.Contains('\r')) { Flush(); result.Add(segment); style = null; continue; }
            if (style == null || !style.Equals(segment.Style)) { Flush(); style = segment.Style; }
            text.Append(segment.Text);
        }
        Flush(); return result;
    }
}

internal sealed class SingleLineText(string text, Style style) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Segment.Truncate(new[] { new Segment(TerminalSafeText.Clean(text).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '), style) }, maxWidth);
}

internal sealed class TranscriptViewport(IReadOnlyList<IRenderable> items, int height, int scroll, Action<int>? changed = null, Func<int, int, int>? preserve = null, IRenderable? target = null,
    Action<int, int>? blockRendered = null) : IRenderable
{
    private sealed class LineEntry { public readonly object Gate = new(); public int Width = -1; public int Version = -1; public long Animation; public Segment[][] Lines = []; }
    private static readonly ConditionalWeakTable<IRenderable, LineEntry> CachedLines = new();
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var natural = new RenderOptions(options.Capabilities, options.ConsoleSize) { Justification = options.Justification, Height = null };
        var groups = items.Select(item => GetLines(item, natural, maxWidth, false)).ToArray();
        var lineCount = groups.Sum(group => group.Length);
        var targetIndex = target == null ? -1 : items.Select((item, index) => (item, index)).Where(pair => ReferenceEquals(pair.item, target)).Select(pair => pair.index).DefaultIfEmpty(-1).First();
        var requested = targetIndex < 0 ? preserve?.Invoke(lineCount, maxWidth) ?? scroll : lineCount - height - groups.Take(targetIndex).Sum(group => group.Length);
        var offset = Math.Clamp(requested, 0, Math.Max(0, lineCount - height));
        changed?.Invoke(offset);
        var start = Math.Max(0, lineCount - height - offset); var end = Math.Min(lineCount, start + height); var cursor = 0;
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            var groupEnd = cursor + group.Length;
            if (groupEnd > start && cursor < end)
            {
                group = GetLines(items[groupIndex], natural, maxWidth, true);
                blockRendered?.Invoke(Math.Max(cursor, start) - start, Math.Min(groupEnd, end) - start);
                for (var i = Math.Max(0, start - cursor); i < Math.Min(group.Length, end - cursor); i++)
                { foreach (var segment in group[i]) yield return segment; yield return Segment.LineBreak; }
            }
            cursor = groupEnd;
            if (cursor >= end) break;
        }
    }
    private static Segment[][] GetLines(IRenderable item, RenderOptions options, int width, bool animate)
    {
        var entry = CachedLines.GetValue(item, _ => new LineEntry()); var version = item is IVersionedRenderable versioned ? versioned.Version : 0;
        lock (entry.Gate)
        {
            var animation = animate && item is IAnimatedRenderable animated ? animated.AnimationVersion : entry.Animation;
            if (entry.Width != width || entry.Version != version || entry.Animation != animation)
            {
                entry.Lines = Segment.SplitLines(item.Render(options, width).Concat(new[] { Segment.LineBreak }), width, null).Select(line => line.ToArray()).ToArray();
                entry.Width = width; entry.Version = version;
                entry.Animation = animation;
            }
            return entry.Lines;
        }
    }
}

internal interface IVersionedRenderable { int Version { get; } }
internal interface IAnimatedRenderable { long AnimationVersion { get; } }

internal sealed class TextViewport(IRenderable content, int height, int offset) : IRenderable
{
    public int Height { get; set; } = height;
    public int Offset { get; set; } = offset;
    public int Width { get; private set; }
    public IReadOnlyList<List<Segment>> Lines { get; private set; } = [];
    public (int X, int Y)? SelectionStart { get; set; }
    public (int X, int Y)? SelectionEnd { get; set; }
    public Color SelectionColor { get; set; }
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (Width != maxWidth)
        {
            var natural = new RenderOptions(options.Capabilities, options.ConsoleSize) { Height = null };
            Lines = Segment.SplitLines(content.Render(natural, maxWidth), maxWidth, null);
            Width = maxWidth;
        }
        Offset = Math.Clamp(Offset, 0, Math.Max(0, Lines.Count - Height));
        var region = new SelectionRegion(0, 0, Width, Lines.Count);
        for (var row = 0; row < Height; row++)
        {
            var y = Offset + row;
            var x = 0;
            if (y < Lines.Count) foreach (var segment in Lines[y])
            {
                if (SelectionStart is not { } first || SelectionEnd is not { } last)
                { yield return segment; continue; }
                var elements = StringInfo.GetTextElementEnumerator(segment.Text);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement(); var cells = new Segment(element).CellCount();
                    yield return new Segment(element, region.Selected(x, y, cells, first, last)
                        ? new Style(Color.Black, SelectionColor, segment.Style.Decoration) : segment.Style);
                    x += cells;
                }
            }
            yield return Segment.LineBreak;
        }
    }
}

/// <summary>A fixed rectangle prevents terminal scrolling and clears stale cells after resize or shorter output.</summary>
internal sealed class ScreenSurface(IRenderable content, int width, int height, Color background, Color foreground = default) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(width, width);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = Segment.SplitLines(new SafeTerminalRenderable(content).Render(options, Math.Min(maxWidth, width)), width, height);
        for (var y = 0; y < height; y++)
        {
            var cells = 0;
            var paddingWidth = y == height - 1 ? width - 1 : width;
            if (y < lines.Count) foreach (var segment in Segment.Truncate(lines[y], paddingWidth))
            {
                cells += segment.CellCount(); var style = segment.Style;
                if (style.Background == Color.Default) style = style.Combine(new Style(background: background));
                if (style.Foreground == Color.Default && foreground != default) style = style.Combine(new Style(foreground));
                yield return new Segment(segment.Text, style);
            }
            if (cells < paddingWidth) yield return new Segment(new string(' ', paddingWidth - cells), new Style(background: background));
            // The bottom-right cell is deliberately excluded to avoid auto-wrap scrolling on Windows.
            if (y < height - 1) yield return Segment.LineBreak;
        }
    }
}
