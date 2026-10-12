using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>Spectre.Console Layout and Live host; standard Spectre prompts share one output owner.</summary>
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
    private int _inspectorScroll, _inspectorSelection;
    private readonly IAnsiConsole _native;
    private readonly object _drawGate = new();
    private readonly SpectreScreenDisplay _display;
    private readonly TerminalKeyReader _keys = new();
    internal TerminalKeyReader Keys => _keys;
    private bool _promptActive;
    private int? _lastFrameKey;
    private IRenderable? _cachedSidebar;
    private long _sidebarTime;
    private Segment[]? _lastSegments;
    private int _lastWidth, _lastHeight;
    private DateTime _skillsChecked;
    private int _enabledSkills;
    private string _status = "准备就绪";
    private IRenderable? _activity, _dialog, _conversationPrompt;
    private InputState _input = new();
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
    public Func<string>? TaskNotice { get; set; }
    public Func<string, BackgroundTaskInfo?>? TaskLookup { get; set; }
    public Func<BackgroundTaskInfo, IRenderable>? TaskTranscript { get; set; }
    public Guid SessionId { get; private set; }
    public string Model { get; private set; } = "未选择模型";
    public TerminalTheme Theme { get; set; }
    private Color Background => Theme.Background;
    private Color Surface => Theme.Surface;
    private Color Accent => Theme.Accent;
    public FullScreenTerminal(DataStore store, SkillCatalog skills, McpToolService mcp, TerminalUi ui, IAnsiConsole? console = null)
    {
        _store = store; _skills = skills; _mcp = mcp; _ui = ui; _native = console ?? AnsiConsole.Console;
        _display = new(_native);
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
    public void Session(Guid id, string model, bool reload = false)
    {
        Model = model;
        if (SessionId == id && !reload) return;
        _ui.RestoreContextUsage(_store.GetConversation(id));
        _cachedSidebar = null;
        SessionId = id; _scroll = _lastRows = _lastTranscriptWidth = 0; _conversationPrompt = null; _transcriptSelectionStart = _transcriptSelectionEnd = null; _dragSelecting = _dragSelectingTranscript = false; _selectionNotice = null; _transcript.Clear(); _executions.Clear(); _traceCells.Clear(); CloseInspector();
        _messageAnchors.Clear(); _jumpTarget = null;
        var blocks = new Dictionary<string, CodeExecutionBlock>();
        foreach (var message in _store.GetVisibleMessages(id))
        {
            if (!message.IsInternal && message.Role is "user" or "assistant" && (!string.IsNullOrEmpty(message.Content) || message.Images.Count > 0))
            {
                var block = Message(message.Role == "user" ? "你" : "LanAgent", message.Role == "user" ? SessionTimeline.Text(message) : message.Content, message.Role == "user");
                _messageAnchors[message.Id] = block; _transcript.Add(block);
            }
            if (!message.IsInternal && message.Images.Count > 0)
                _transcript.Add(ImageAttachmentsView.Render(message.Images));
            try
            {
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
                        foreach (var trace in JsonSerializer.Deserialize<HostToolTrace[]>(message.HostToolTraceJson, LanAgent.Core.Tools.ToolResult.JsonOptions) ?? [])
                        { completed.Start(new(trace.Id, trace.ToolName, trace.ArgsJson)); completed.Complete(new(trace.Id, trace.ToolName, trace.ResultJson)); }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { /* Older or interrupted traces must not prevent restoring chat. */ }
        }
    }
    private IRenderable Message(string label, string content, bool user) => TerminalMarkdown.Message(label, content, user);
    public void UserMessage(string message) => Append(Message("你", message, true));
    public void Append(IRenderable renderable)
    {
        _transcript.Add(renderable); if (_transcript.Count > 800) _transcript.RemoveRange(0, 100);
        // Do not jump to the bottom while the user is reading earlier messages.
    }
    public void RegisterExecution(CodeExecutionBlock block)
    {
        block.UseMarkers = true;
        block.TaskLookup = TaskLookup;
        block.TaskTranscript = TaskTranscript;
        _executions[block.TraceId] = block;
        if (_executions.Count > 300) _executions.Remove(_executions.Keys.First());
    }
    internal bool InspectLatest()
    {
        if (_executions.Values.LastOrDefault() is not { } block) return false;
        OpenInspector(block, -1); return true;
    }
    private void OpenInspector(CodeExecutionBlock block, int span) { _inspector = (block, span); _inspectorScroll = _inspectorSelection = 0; }
    private void CloseInspector() { _inspector = null; _inspectorScroll = _inspectorSelection = 0; }
    internal bool InspectorInput(TerminalInput input)
    {
        if (_inspector == null) return false;
        if (input.Key.Key == ConsoleKey.C && input.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) return false;
        if (input.Key.Key is ConsoleKey.Escape or ConsoleKey.Enter) { CloseInspector(); return true; }
        if (input.Wheel != 0) _inspectorScroll = Math.Max(0, _inspectorScroll - input.Wheel);
        else if (input.Key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp) _inspectorScroll = Math.Max(0, _inspectorScroll - (input.Key.Key == ConsoleKey.PageUp ? 8 : 1));
        else if (input.Key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown) _inspectorScroll += input.Key.Key == ConsoleKey.PageDown ? 8 : 1;
        else if (input.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            var trace = _inspector.Value;
            var count = trace.Span < 0 ? trace.Block.Invocations.Count : trace.Block.Invocations.Count(call => call.SpanIndex == trace.Span);
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
            if (!_messageAnchors.TryGetValue(message, out target)) throw new ArgumentException("无法定位这条消息。");
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
        if (_inspector != null || _conversationPrompt != null) return true;
        if (_dialog == null && pointer.Action == PointerAction.Down && _traceCells.TryGetValue((pointer.X, pointer.Y), out var trace)) { OpenInspector(trace.Block, trace.Span); return true; }
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
        if (state.HasSelection) { state.CopySelection(); return true; }
        if (_transcriptSelectionStart != _transcriptSelectionEnd && _transcriptSelectionStart != null)
        { CopyTranscriptSelection(new('\0', ConsoleKey.C, false, false, true)); return true; }
        if (_dialog != null || _inspector != null || _conversationPrompt != null || !IsInputPoint(pointer.X, pointer.Y)) return true;
        var reading = Task.Run(_keys.ReadClipboard);
        while (!reading.IsCompleted) { Draw(); await Task.WhenAny(reading, Task.Delay(8)); }
        var clipboard = await reading;
        if (clipboard != null) await paste(clipboard);
        else state.InsertPaste("", "剪贴板没有可粘贴的文字或图片");
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
        var block = selected.Value; var column = 0; var separators = new List<int>();
        string Row(int row) => string.Concat(_selectionLines[row].Select(segment => segment.Text));
        if (Row(block.Top).Any(c => c is '┏' or '┌' or '╭' or '╔')) block = block with { Top = Math.Min(block.Top + 1, block.Bottom - 1) };
        if (Row(block.Bottom - 1).Any(c => c is '┗' or '└' or '╰' or '╚')) block = block with { Bottom = Math.Max(block.Top + 1, block.Bottom - 1) };
        foreach (var segment in _selectionLines[y])
        {
            var chars = StringInfo.GetTextElementEnumerator(segment.Text);
            while (chars.MoveNext())
            {
                var element = chars.GetTextElement();
                if (column >= block.Left && column < block.Right && element is "│" or "┃" or "║") separators.Add(column);
                column += new Segment(element).CellCount();
            }
        }
        // A table column or panel is its own horizontal selection block.
        var left = separators.Where(position => position < x).DefaultIfEmpty(block.Left - 1).Max() + 1;
        var right = separators.Where(position => position >= x).DefaultIfEmpty(block.Right).Min();
        return right > left ? block with { Left = left, Right = right } : null;
    }
    private bool IsTranscriptPoint(int x, int y) => x >= _transcriptLeft && x < _transcriptRight && y >= _transcriptTop && y < _transcriptTop + _transcriptHeight;
    private (int X, int Y) ClampTranscriptPoint(int x, int y) => (Math.Clamp(x, _transcriptLeft, Math.Max(_transcriptLeft, _transcriptRight - 1)), Math.Clamp(y, _transcriptTop, Math.Max(_transcriptTop, _transcriptTop + _transcriptHeight - 1)));
    public bool CopyTranscriptSelection(ConsoleKeyInfo key)
    {
        if (key.Key != ConsoleKey.C || !key.Modifiers.HasFlag(ConsoleModifiers.Control) || _transcriptSelectionStart is not { } start || _transcriptSelectionEnd is not { } end) return false;
        var selected = ExtractSelection(start, end);
        if (selected.Length == 0) { _selectionNotice = "请先拖选消息文字"; return true; }
        _selectionNotice = ClipboardText.Write(selected) ? "已复制选中消息文字" : "剪贴板暂不可用";
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
            return new ScreenSurface(new Rows(new Text("LanAgent", new Style(Accent, decoration: Decoration.Bold)), new Text("请放大终端窗口至至少 40 列 × 16 行。", new Style(Color.Grey)), new Text("输入和任务会保留；Esc 返回，Ctrl+C 取消。", new Style(Color.Grey))), width, height, Background, Theme.Foreground);
        }
        var narrow = width < 100 || !ShowSidebar; var sidebarWidth = narrow ? 0 : 29;
        var inputRows = new InputLayout(_input.Text, Math.Max(1, width - sidebarWidth - 4)).Rows.Count;
        var inputHeight = Math.Clamp(inputRows + 2, 5, Math.Min(9, Math.Max(5, height / 3)));
        var hintsHeight = _hints.Count > 0 ? Math.Min(6, _hints.Count + 1) : 0;
        if (height < 24) hintsHeight = Math.Min(3, hintsHeight);
        var bodyHeight = Math.Max(1, height - 2 - inputHeight - hintsHeight - 2);
        _hintsTop = height - 2 - inputHeight - hintsHeight; _hintsHeight = hintsHeight == 0 ? 0 : hintsHeight - 1;
        _transcriptTop = 3; _transcriptHeight = Math.Max(1, bodyHeight - 2); _transcriptLeft = 1; _transcriptRight = Math.Max(2, width - sidebarWidth - 1);
        var conversation = _store.GetConversation(SessionId);
        var title = conversation?.Title is { Length: > 0 } text && text != "New Conversation" ? text : "开始新的对话";
        var layout = new Layout("screen").SplitRows(new Layout("header").Size(2), new Layout("main"), new Layout("footer").Size(2));
        layout["header"].Update(new Rows(new SingleLineText(" LanAgent │ " + title, new Style(Accent, decoration: Decoration.Bold)), new SingleLineText(" " + _ui.Workspace, new Style(Color.Grey))));
        var dialog = _dialog;
        if (dialog == null && _inspector is { } inspection)
        {
            var task = inspection.Block.SelectedTask(inspection.Span, _inspectorSelection);
            var child = task != null && (task.Kind == "agent" || task.Kind != "shell" && task.ParentSessionId != null);
            dialog = new Panel(new TextViewport(inspection.Block.Details(inspection.Span, _inspectorSelection), Math.Max(2, height - 10), _inspectorScroll)).Header(child ? "子代理消息 · " + Markup.Escape(TerminalSafeText.Clean(task!.Title)) : "工具执行详情").Border(BoxBorder.Rounded).BorderColor(Accent).Padding(1, 1);
        }
        if (dialog != null)
        {
            layout["main"].Update(new Align(dialog, HorizontalAlignment.Center, VerticalAlignment.Middle));
            layout["footer"].Update(new Text(_inspector != null && _dialog == null ? " 滚轮/↑↓ 查看 · ←→ 切换实际调用 · Esc/Enter 关闭" : " ↑↓ 选择 · Enter 确认 · Esc 返回 / 拒绝", new Style(Color.Grey)));
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
        else if (messageRows.Count == 0) main = new Align(new Rows(new FigletText("LanAgent").Color(Accent), new Text("直接描述任务，Agent 会使用工具并按需派发子代理。", new Style(Color.Grey)), new Text("\nF2 选择模型   输入 / 查看常用操作\nCtrl+J 换行   @ 引用文件   Esc 停止任务", new Style(Color.Grey)), new Text("\n第一次使用：F2 配置模型，或 /login 登录。", new Style(Accent))), HorizontalAlignment.Center, VerticalAlignment.Middle);
        else main = new TranscriptViewport(messageRows, Math.Max(1, bodyHeight - 2), _scroll, value => { _scroll = value; _jumpTarget = null; }, PreserveScroll, _jumpTarget,
            (top, bottom) => _visibleBlocks.Add(new(_transcriptLeft, _transcriptTop + top, _transcriptRight, _transcriptTop + bottom)));
        left["messages"].Update(new Padder(main, new Padding(1, 1)));
        if (_hints.Count > 0)
        {
            var hints = new List<IRenderable>();
            var selected = Math.Clamp(_input.Selection, 0, _hints.Count - 1);
            _hintStart = Math.Max(0, selected - 3);
            foreach (var (hint, index) in _hints.Select((h, i) => (h, i)).Skip(Math.Max(0, selected - 3)).Take(Math.Max(1, hintsHeight - 1)))
                hints.Add(new Markup(hint.MarkerMarkup + (index == selected ? "[black on #7dcfff] › " : "[dim]   ") + Markup.Escape(hint.Text + "  " + hint.Description) + "[/]"));
            left["suggestions"].Update(new Rows(hints));
        }
        _inputText = new InputText(_input, maxRows: inputHeight - 2); _inputX = 2; _inputY = height - 2 - inputHeight + 1; _inputWidth = width - sidebarWidth - 4;
        left["input"].Update(new Panel(_inputText).Header("[bold #7dcfff] " + (string.IsNullOrEmpty(_input.Text) ? "输入任务" : "正在输入") + " [/]").Border(BoxBorder.Heavy).BorderColor(Accent).Padding(1, 0).Expand());
        if (!narrow) layout["sidebar"].Update(new ScreenSurface(new Panel(Sidebar()).Border(BoxBorder.None).Padding(1, 1).Expand(), sidebarWidth, height - 4, Surface));
        var imageLabel = PromptReferences.ImageDraftLabel(_input.Text);
        if (conversation?.Revert != null) imageLabel = "已暂存撤回 · /redo 恢复原历史和文件 · 提交新任务后丢弃被撤回的历史" + (imageLabel.Length > 0 ? " · " + imageLabel : "");
        layout["footer"].Update(new Rows(new SingleLineText(" " + _ui.ContextLabel + " │ " + TaskNotice?.Invoke() + (_scroll > 0 ? "阅读历史 ↑ · PgDn | " : "") + (_selectionNotice ?? _input.Notice ?? _status), new Style(_ui.ContextUsedTokens >= _ui.ContextCapacityTokens * 0.85 ? Color.Yellow : Color.Grey)), new SingleLineText(" " + (imageLabel.Length > 0 ? imageLabel : "F1 菜单  F2 模型  Ctrl+E 执行详情  消息区拖选文字 · Ctrl+C 复制 | 输入区 Shift+方向选择"), new Style(Color.Grey))));
        return new ScreenSurface(layout, width, height, Background, Theme.Foreground);
    }
    private IRenderable Sidebar()
    {
        var now = Environment.TickCount64;
        if (_cachedSidebar != null && now - _sidebarTime < 1000) return _cachedSidebar;
        _sidebarTime = now;
        var sessions = SessionOperations.MainSessions(_store).Take(5).ToArray();
        var c = _store.GetConversation(SessionId);
        var messages = _store.GetMessages(SessionId); var characters = messages.Sum(m => m.Content.Length + (m.ToolCallsJson?.Length ?? 0));
        var cacheRate = c is { CacheUsageRequests: > 0, CacheInputTokens: > 0 }
            ? $"{Math.Min(c.CachedInputTokens, c.CacheInputTokens) * 100.0 / c.CacheInputTokens:F1}%"
            : "暂无数据";
        var rows = new List<IRenderable> { new Markup("[bold]会话信息[/]"), new Text(Model, new Style(Accent)).Fold(), new Text("模型缓存命中率 · " + cacheRate, new Style(Color.Grey)), new Text($"{messages.Count} 条消息 · {characters:N0} 字符\n上下文为字符估算", new Style(Color.Grey)), new Rule().RuleStyle("grey"), new Markup("[bold]最近会话 [dim]F3[/][/]") };
        foreach (var session in sessions) rows.Add(new Text((session.Id == SessionId ? "› " : "  ") + SessionOperations.SingleLineTitle(session.Title), new Style(session.Id == SessionId ? Accent : Color.Grey)).Crop());
        rows.Add(new Rule().RuleStyle("grey")); rows.Add(new Markup("[bold]扩展[/]"));
        if (DateTime.UtcNow - _skillsChecked > TimeSpan.FromSeconds(1)) { _enabledSkills = _skills.CachedList.Count(s => s.Enabled); _skillsChecked = DateTime.UtcNow; }
        rows.Add(new Text($"Skills · {_enabledSkills} 个启用  [F4]", new Style(Color.Grey)));
        rows.Add(new Text($"MCP · {_mcp.GetAllServerTools().Count} 个连接  [F5]", new Style(Color.Grey)));
        if (ExtensionSummary != null) rows.Add(new Text(ExtensionSummary(), new Style(Accent)));
        foreach (var server in _store.McpServerConfigs.Take(4)) rows.Add(new Text(($"{(_mcp.IsConnected(server.Id) ? "●" : "○")} {server.Name}"), new Style(_mcp.IsConnected(server.Id) ? Color.Green : Color.Grey)).Crop());
        rows.Add(new Rule().RuleStyle("grey")); rows.Add(new Markup("[bold]操作[/]"));
        rows.Add(new Text("F1  打开操作菜单\nF2  切换模型 / 提供商\nF3  恢复会话\nF6  思考强度\nTab 规划 / 执行\nCtrl+B 隐藏侧栏\nCtrl+P 搜索所有命令", new Style(Color.Grey)));
        return _cachedSidebar = new Rows(rows);
    }
    internal static IRenderable Input(InputState state, bool secret = false)
    {
        return new InputText(state, secret);
    }
    public void Draw()
    {
        if (!_entered || _promptActive) return;
        lock (_drawGate)
        {
            var width = Math.Clamp(Console.WindowWidth, 1, 500); var height = Math.Clamp(Console.WindowHeight, 1, 200);
            var now = Environment.TickCount64;
            var key = new HashCode();
            key.Add(width); key.Add(height); key.Add(Theme); key.Add(Model); key.Add(SessionId); key.Add(ShowSidebar);
            key.Add(_scroll); key.Add(_status); key.Add(_selectionNotice); key.Add(_ui.ContextLabel);
            key.Add(_input.Text); key.Add(_input.Cursor); key.Add(_input.Anchor); key.Add(_input.Selection); key.Add(_input.Notice);
            key.Add(_transcriptSelectionStart); key.Add(_transcriptSelectionEnd); key.Add(_inspector); key.Add(_inspectorScroll); key.Add(_inspectorSelection);
            key.Add(_dialog); key.Add(_conversationPrompt); key.Add(now / 1000);
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
            _display.Update(new SegmentFrame(segments, width));
        }
    }
    internal Segment[] RenderFrame(int width, int height)
    {
        _visibleBlocks.Clear();
        var segments = BuildFrame(width, height).Render(RenderOptions.Create(_native, _native.Profile.Capabilities), width).ToArray();
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
    public async Task<T> BusyAsync<T>(string title, Func<CancellationToken, Task<T>> action, CancellationToken ct = default, Func<string>? titleProvider = null, bool allowEscape = false)
    {
        // Environment installation can itself ask for permission; keep that flow in the shared live host.
        if (!allowEscape && !title.Contains("环境", StringComparison.Ordinal))
            return await WithPromptAsync(token => _native.Status().Spinner(LoadingIndicator.Current).SpinnerStyle(new Style(Accent))
                .StartAsync(Markup.Escape(title + " · Ctrl+C 取消"), async _ => await action(token)), ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var keys = allowEscape ? _keys : null;
        var cancelConfirmation = new EscapeCancelConfirmation();
        try
        {
            var task = action(cancelled.Token);
            while (!task.IsCompleted)
            {
                var glyph = LoadingIndicator.Frame(watch.ElapsedMilliseconds);
                SetActivity(new Text($"{glyph} {titleProvider?.Invoke() ?? title}… {watch.Elapsed.TotalSeconds:F0}s · {(cancelConfirmation.Armed ? "再次按 Esc 确认取消 · " : allowEscape ? "双击 Esc / " : "")}Ctrl+C 取消", new Style(Accent))); Draw();
                if (keys?.HasInput == true)
                {
                    var key = await keys.ReadAsync(cancelled.Token);
                    if (key.Paste == null && key.Key.Key == ConsoleKey.Escape) { if (cancelConfirmation.Read(key.Key)) cancelled.Cancel(); }
                    else { cancelConfirmation.Reset(); Navigation(key); }
                }
                await Task.WhenAny(task, Task.Delay(33, CancellationToken.None));
            }
            return await task;
        }
        finally { SetActivity(null); Console.CancelKeyPress -= handler; Draw(); }
    }
    public async Task<string> ReadInputAsync(InputEditor editor, string status, string initial, CancellationToken ct)
    {
        using var inputMode = new TextInputMode(); _dragSelecting = false;
        _activity = null; var state = new InputState(); state.Set(initial); _ui.Draft = ""; var input = _keys; var queueShortcut = new QueueShortcut();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                SetInput(state, editor.Hints(state), status); Draw();
                while (!input.HasInput && !cancel.IsCancellationRequested)
                {
                    if (editor.WakeRequested?.Invoke() == true) { _ui.Draft = state.Text; editor.InterruptedDraft = state.Text; return ""; }
                    await Task.Delay(8, cancel.Token); SetInput(state, editor.Hints(state), status); Draw();
                }
                var key = await ReadKeyAsync(input, cancel.Token);
                var queueChord = key.Paste == null ? queueShortcut.Read(key.Key, state.HasSelection, timeline: true) : 0;
                if (queueChord == 1) continue;
                if (queueChord == 2 && _ui.Inbox != null)
                {
                    var restored = await PendingInputsAsync(_ui.Inbox, state.Text, cancel.Token);
                    if (restored != null) { state.SelectAll(); state.Insert(restored); }
                    continue;
                }
                if (queueChord == 3)
                {
                    try
                    {
                        var choice = await TimelineAsync(cancel.Token);
                        if (choice != null) { _ui.Draft = state.Text; return "/timeline " + choice.Action + " " + choice.MessageId; }
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                    continue;
                }
                if (InspectorInput(key)) continue;
                if (key.Key.Key == ConsoleKey.E && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control) && InspectLatest()) continue;
                if (key.Paste != null) { await editor.PasteAsync(state, key.Paste, cancel.Token); continue; }
                if (await ContextClickAsync(key, state, text => editor.PasteAsync(state, text, cancel.Token))) continue;
                if (SelectInput(key, state)) continue;
                if (key.Key.Key == ConsoleKey.C && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control) && state.HasSelection) { state.Key(key.Key, editor.Hints(state), editor.History); continue; }
                if (CopyTranscriptSelection(key.Key)) continue;
                if (Navigation(key)) continue;
                string? shortcut = null;
                try
                {
                    shortcut = key.Key.Key switch { ConsoleKey.F1 => await MenuAsync(cancel.Token), ConsoleKey.F2 => "/model", ConsoleKey.F3 => "/resume", ConsoleKey.F4 => "/skills", ConsoleKey.F5 => "/mcp", ConsoleKey.F6 => "/effort", _ => null };
                    if (key.Key.Key == ConsoleKey.P && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) shortcut = await PaletteAsync(editor, cancel.Token);
                    if (key.Key.Key == ConsoleKey.T && key.Key.Modifiers.HasFlag(ConsoleModifiers.Control)) shortcut = "/effort next";
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { continue; }
                if (key.Key.Key == ConsoleKey.Tab && state.Text.Length == 0) shortcut = "/plan";
                if (shortcut != null) { _ui.Draft = state.Text; return shortcut; }
                var submitted = state.Key(key.Key, editor.Hints(state), editor.History);
                if (submitted != null) { if (submitted.Length > 0) ScrollToLatest(); if (submitted.Length > 0 && !submitted.StartsWith('/')) { UserMessage(submitted); SetInput(new InputState(), [], "任务已提交 · 准备 Agent"); Draw(); } return submitted; }
            }
            return "";
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    private async Task<string?> MenuAsync(CancellationToken ct)
    {
        var entries = new[] { ("继续聊天", ""), ("开始新任务", "/new"), ("恢复历史会话", "/resume"), ("选择 / 配置模型", "/model"), ("账号登录", "/login"), ("子代理与后台任务", "/tasks"), ("撤销上一轮任务", "/undo"), ("权限模式", "/permissions"), ("显示与主题设置", "/settings"), ("全部操作与快捷键", "/help all"), ("退出并保留会话", "/exit") };
        var selected = await ChooseAsync("操作菜单", entries, e => e.Item1, ct); return selected.Item2.Length > 0 ? selected.Item2 : null;
    }
    private async Task<string?> PaletteAsync(InputEditor editor, CancellationToken ct)
    {
        var choice = await ChooseAsync("命令面板 · 输入关键字搜索", editor.Commands().ToArray(), h => h.Text + " · " + h.Description, ct); return choice.Text;
    }
    public T Choose<T>(string title, IReadOnlyList<T> choices, Func<T,string> label) => ChooseAsync(title, choices, label, CancellationToken.None).GetAwaiter().GetResult();
    internal sealed record TimelineChoice(Guid MessageId, string Action);
    public async Task<TimelineChoice?> TimelineAsync(CancellationToken ct = default)
    {
        Session(SessionId, Model, reload: true);
        var chosen = await WithPromptAsync<Guid?>(async token =>
        {
            var selected = 0; var search = new InputState();
            while (true)
            {
                var entries = SessionTimeline.Entries(_store, SessionId).Where(message => SessionTimeline.Text(message).Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                    || message.Id.ToString().StartsWith(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
                selected = Math.Clamp(selected, 0, Math.Max(0, entries.Length - 1));
                if (entries.Length > 0) JumpToMessage(entries[selected].Id);
                _native.Clear(); _native.Write(new Rule("[bold]会话时间线[/]").RuleStyle(new Style(Accent)));
                _native.Write(new SingleLineText("搜索 › " + search.Text, new Style(Accent))); _native.WriteLine();
                var rows = Math.Max(1, _native.Profile.Height - 7); var start = Math.Max(0, selected - rows + 1);
                foreach (var (message, index) in entries.Select((message, index) => (message, index)).Skip(start).Take(rows))
                {
                    var label = (index == selected ? "› " : "  ") + message.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + message.Id.ToString()[..8] + " · " + SessionTimeline.Text(message).Replace('\n', ' ');
                    _native.Write(new SingleLineText(TerminalSafeText.Clean(label), new Style(index == selected ? Accent : Color.Grey))); _native.WriteLine();
                }
                if (entries.Length == 0) _native.WriteLine("没有匹配的用户消息。");
                _native.Write(new Text("↑↓ 选择并定位 · 输入搜索 · Enter 消息操作 · Esc 返回会话\n", new Style(Color.Grey)));
                var input = await _keys.ReadAsync(token); var key = input.Key;
                if (input.Paste != null) { search.Insert(input.Paste); selected = 0; continue; }
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return null;
                if (key.Key == ConsoleKey.UpArrow) selected = Math.Max(0, selected - 1);
                else if (key.Key == ConsoleKey.DownArrow) selected = Math.Min(entries.Length - 1, selected + 1);
                else if (key.Key == ConsoleKey.Enter) { if (entries.Length > 0) return entries[selected].Id; }
                else { search.Key(key, [], []); selected = 0; }
            }
        }, ct);
        if (chosen == null) return null;
        var actions = new[] { ("跳转到消息", "jump"), ("复制消息正文", "copy"), ("恢复到草稿", "restore"), ("从此消息之前分支", "fork"), ("撤回此消息起的任务和文件修改", "revert") };
        var action = await ChooseAsync("消息操作", actions, item => item.Item1, ct);
        return new(chosen.Value, action.Item2);
    }
    public async Task<string?> PendingInputsAsync(InputInbox inbox, string draft, CancellationToken ct)
    {
        try
        {
            return await WithPromptAsync<string?>(async token =>
            {
                var selected = 0; string? notice = null;
                while (true)
                {
                    var items = inbox.Pending;
                    if (items.Count == 0) return null;
                    selected = Math.Clamp(selected, 0, items.Count - 1);
                    _native.Clear();
                    _native.Write(new Rule("[bold]待处理输入[/]").RuleStyle(new Style(Accent)));
                    var start = Math.Max(0, selected - Math.Max(1, _native.Profile.Height - 7) + 1);
                    foreach (var (item, index) in items.Select((item, index) => (item, index)).Skip(start).Take(Math.Max(1, _native.Profile.Height - 7)))
                    {
                        var label = (index == selected ? "› " : "  ") + item.Id.ToString()[..8] + " · " + item.Delivery + " · " + item.Draft.Replace('\n', ' ') + (item.Images.Count > 0 ? $" · 图片 {item.Images.Count}" : "");
                        _native.Write(new SingleLineText(TerminalSafeText.Clean(label), new Style(index == selected ? Accent : Color.Grey))); _native.WriteLine();
                    }
                    _native.Write(new Text("Enter 当前任务处理 · Tab 移回队列 · Ctrl+D/Delete 删除 · Ctrl+U/Z 恢复草稿 · Esc 返回\n", new Style(Color.Grey)));
                    if (notice != null) _native.Write(new Text(notice + "\n", new Style(Color.Yellow)));
                    var input = await _keys.ReadAsync(token); var key = input.Key;
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
                        var restored = await inbox.CommandAsync("/queue " + action + " " + items[selected].Id, draft, token);
                        if (action is "steer" or "edit") return restored;
                        notice = action == "delete" ? "已删除待处理输入。" : "已移回队列。";
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                    { notice = ex.Message; }
                }
            }, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
    private sealed record PromptChoice<T>(T Value);
    public async Task<T> ChooseAsync<T>(string title, IReadOnlyList<T> choices, Func<T,string> label, CancellationToken ct)
    {
        if (choices.Count == 0) throw new ArgumentException("没有可选项。");
        return await WithPromptAsync(async token =>
        {
            var prompt = new SelectionPrompt<PromptChoice<T>>()
                .Title(Markup.Escape(TerminalSafeText.Clean(title)))
                .PageSize(Math.Clamp(_native.Profile.Height - 8, 3, 14))
                .HighlightStyle(new Style(Accent, decoration: Decoration.Bold))
                .UseConverter(choice => Markup.Escape(TerminalSafeText.Clean(label(choice.Value))))
                .EnableSearch().SearchPlaceholderText("[dim]输入关键字搜索 · ↑↓ 选择 · Enter 确认 · Esc 返回[/]")
                .WrapAround().AddChoices(choices.Select(choice => new PromptChoice<T>(choice)));
            prompt.CancelResult = () => throw new OperationCanceledException("已返回。");
            return (await new SpectrePromptConsole(_native, _entered ? _keys : null).PromptAsync(prompt, token)).Value;
        }, ct);
    }
    public async Task<string?> QuestionAsync(LanAgent.Core.Tools.AgentQuestion question, CancellationToken ct)
    {
        try { return await WithPromptAsync(token => TerminalQuestionPrompt.AskAsync(_native, _entered ? _keys : new TerminalKeyReader(_native.Input.IsKeyAvailable, () => _native.Input.ReadKey(true) ?? default), question, token), ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task<T> WithPromptAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _promptCancellation = cancellation;
        lock (_drawGate)
        {
            _promptActive = true;
            _display.Stop();
            if (_entered) { Console.Write("\u001b[?2004h\u001b[?1002h\u001b[?1006h"); _native.Clear(); }
        }
        try { return await action(cancellation.Token); }
        finally
        {
            lock (_drawGate)
            {
                _promptCancellation = null;
                _promptActive = false;
                _lastFrameKey = null; _lastSegments = null;
                if (_entered)
                {
                    ConsoleModes.EnableVirtualTerminal();
                    _native.Clear();
                    Console.Write("\u001b[?2004h\u001b[?1002h\u001b[?1006h");
                    _display.Start(new Text(""));
                }
            }
            Draw();
        }
    }
    public async Task<string> TextAsync(string title, string? defaultValue, bool secret, bool empty, CancellationToken ct = default, IRenderable? body = null)
    {
        return await WithPromptAsync(async token =>
        {
            if (body != null) _native.Write(new Panel(new TextViewport(body, Math.Max(2, _native.Profile.Height - 8), 0)).Border(BoxBorder.Rounded).BorderColor(Accent));
            _native.Write(new Rule("[bold]" + Markup.Escape(TerminalSafeText.Clean(title)) + "[/]").RuleStyle(new Style(Accent)));
            _native.MarkupLine("[dim]Enter 保存 · Esc 返回 · Ctrl+V 粘贴[/]");
            var prompt = new TextPrompt<string>("›").PromptStyle(new Style(Accent));
            prompt.ClearOnFinish = true;
            if (empty) prompt.AllowEmpty();
            else prompt.Validate(value => !string.IsNullOrWhiteSpace(value), "[yellow]此项不能为空。[/]");
            if (secret) prompt.Secret('•').HideDefaultValue();
            else if (defaultValue != null) prompt.DefaultValue(defaultValue).EditableDefaultValue(true);
            return await new SpectrePromptConsole(_native, _entered ? _keys : null).PromptAsync(prompt, token);
        }, ct);
    }
    public async Task<bool> ConfirmAsync(string title, CancellationToken ct = default) => await ChooseAsync(title, new[] { "返回 / 拒绝", "确认" }, c => c, ct) == "确认";
    public async Task<bool> ApproveAsync(ApprovalPreview preview, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); _promptCancellation = cancellation;
        var reader = _keys; var selected = 0; var scroll = 0; var expanded = false;
        try
        {
            while (true)
            {
                SetConversationPrompt(new Panel(preview.Render(selected, scroll, expanded, 2)).Header("需要授权").Border(BoxBorder.Rounded).BorderColor(Color.Yellow)); Draw();
                var input = await ReadKeyAsync(reader, cancellation.Token); var key = input.Key;
                if (input.Wheel != 0) { scroll = Math.Clamp(scroll - input.Wheel, 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (input.Paste != null) continue;
                if (key.Key is ConsoleKey.Escape or ConsoleKey.N) return false;
                if (key.Key == ConsoleKey.Y) return true;
                if (key.Key is ConsoleKey.A or ConsoleKey.D3 && preview.RememberForSession != null) { preview.RememberForSession(); return true; }
                if (key.Key == ConsoleKey.Tab) { expanded = !expanded; continue; }
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) { scroll = Math.Clamp(scroll + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, Math.Max(0, preview.Lines.Count - 1)); continue; }
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) { var count = preview.RememberForSession == null ? 2 : 3; selected = (selected + (key.Key == ConsoleKey.UpArrow ? -1 : 1) + count) % count; }
                if (key.Key == ConsoleKey.Enter) { if (selected == 2) preview.RememberForSession?.Invoke(); return selected != 1; }
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
            if (segment.Text.Contains('\n') || segment.Text.Contains('\r')) { Flush(); result.Add(segment); style = null; continue; }
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
    private sealed class LineEntry { public readonly object Gate = new(); public int Width = -1; public int Version = -1; public Segment[][] Lines = []; }
    private static readonly ConditionalWeakTable<IRenderable, LineEntry> CachedLines = new();
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var natural = new RenderOptions(options.Capabilities, options.ConsoleSize) { Justification = options.Justification, Height = null };
        var groups = items.Select(item => GetLines(item, natural, maxWidth)).ToArray();
        var lineCount = groups.Sum(group => group.Length);
        var targetIndex = target == null ? -1 : items.Select((item, index) => (item, index)).Where(pair => ReferenceEquals(pair.item, target)).Select(pair => pair.index).DefaultIfEmpty(-1).First();
        var requested = targetIndex < 0 ? preserve?.Invoke(lineCount, maxWidth) ?? scroll : lineCount - height - groups.Take(targetIndex).Sum(group => group.Length);
        var offset = Math.Clamp(requested, 0, Math.Max(0, lineCount - height));
        changed?.Invoke(offset);
        var start = Math.Max(0, lineCount - height - offset); var end = Math.Min(lineCount, start + height); var cursor = 0;
        foreach (var group in groups)
        {
            var groupEnd = cursor + group.Length;
            if (groupEnd > start && cursor < end)
            {
                blockRendered?.Invoke(Math.Max(cursor, start) - start, Math.Min(groupEnd, end) - start);
                for (var i = Math.Max(0, start - cursor); i < Math.Min(group.Length, end - cursor); i++)
                { foreach (var segment in group[i]) yield return segment; yield return Segment.LineBreak; }
            }
            cursor = groupEnd;
            if (cursor >= end) break;
        }
    }
    private static Segment[][] GetLines(IRenderable item, RenderOptions options, int width)
    {
        var entry = CachedLines.GetValue(item, _ => new LineEntry()); var version = item is IVersionedRenderable versioned ? versioned.Version : 0;
        lock (entry.Gate)
        {
            if (entry.Width != width || entry.Version != version)
            {
                entry.Lines = Segment.SplitLines(item.Render(options, width).Concat(new[] { Segment.LineBreak }), width, null).Select(line => line.ToArray()).ToArray();
                entry.Width = width; entry.Version = version;
            }
            return entry.Lines;
        }
    }
}

internal interface IVersionedRenderable { int Version { get; } }

internal sealed class TextViewport(IRenderable content, int height, int offset) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var natural = new RenderOptions(options.Capabilities, options.ConsoleSize) { Height = null };
        var lines = Segment.SplitLines(content.Render(natural, maxWidth), maxWidth, null);
        foreach (var line in lines.Skip(Math.Min(offset, Math.Max(0, lines.Count - height))).Take(height))
        { foreach (var segment in line) yield return segment; yield return Segment.LineBreak; }
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
