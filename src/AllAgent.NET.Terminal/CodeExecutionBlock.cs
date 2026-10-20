using System.Text.Json;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

internal sealed record PythonToolSpan(int Start, int End, string Name);

/// <summary>Display lexer only. The trusted Python AST validator remains the execution authority.</summary>
internal static class PythonToolSpans
{
    public static IReadOnlyList<PythonToolSpan> Parse(string source)
    {
        var spans = new List<PythonToolSpan>();
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '#') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (source[i] is '\'' or '"') { i = SkipString(source, i) - 1; continue; }
            const string token = "call_tool";
            if (!source.AsSpan(i).StartsWith(token) || i > 0 && (char.IsLetterOrDigit(source[i - 1]) || source[i - 1] is '_' or '.')) continue;
            var open = i + token.Length;
            while (open < source.Length && char.IsWhiteSpace(source[open])) open++;
            if (open >= source.Length || source[open] != '(') continue;
            var nameStart = open + 1; while (nameStart < source.Length && char.IsWhiteSpace(source[nameStart])) nameStart++;
            var name = "call_tool";
            if (nameStart < source.Length && source[nameStart] is '\'' or '"')
            {
                var end = SkipString(source, nameStart);
                if (end > nameStart + 1) name = source[(nameStart + 1)..(end - 1)];
            }
            var depth = 1; var cursor = open + 1;
            while (cursor < source.Length && depth > 0)
            {
                if (source[cursor] is '\'' or '"') { cursor = SkipString(source, cursor); continue; }
                if (source[cursor] == '#') { while (cursor < source.Length && source[cursor] != '\n') cursor++; continue; }
                if (source[cursor] == '(') depth++; else if (source[cursor] == ')') depth--;
                cursor++;
            }
            spans.Add(new(i, cursor, name)); i = cursor - 1;
        }
        return spans;
    }
    private static int SkipString(string source, int start)
    {
        var quote = source[start]; var triple = start + 2 < source.Length && source[start + 1] == quote && source[start + 2] == quote;
        var cursor = start + (triple ? 3 : 1);
        while (cursor < source.Length)
        {
            if (source[cursor] == '\\') { cursor = Math.Min(source.Length, cursor + 2); continue; }
            if (source[cursor] == quote && (!triple || cursor + 2 < source.Length && source[cursor + 1] == quote && source[cursor + 2] == quote)) return cursor + (triple ? 3 : 1);
            cursor++;
        }
        return cursor;
    }
}

internal sealed class ToolInvocation(string id, string name, string args, int spanIndex)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Args { get; } = args;
    public int SpanIndex { get; } = spanIndex;
    public string? Result { get; set; }
    public bool? Ok { get; set; }
}

internal sealed class CodeExecutionBlock(string id, string code, bool reasoning = false) : IRenderable, IVersionedRenderable, IAnimatedRenderable
{
    private static int _nextMarker;
    private readonly Dictionary<int, Color> _markers = [];
    private readonly Dictionary<string, int> _childMarkers = [];
    private readonly Dictionary<int, (int Span, int Selected)> _childTargets = [];
    private IRenderable? _content;
    private int _version;
    private int _taskVersion;
    public Func<string, BackgroundTaskInfo?>? TaskLookup { get; set; }
    public Func<string, IReadOnlyList<BackgroundTaskInfo>>? InvocationTasks { get; set; }
    public Func<BackgroundTaskInfo, IRenderable>? TaskTranscript { get; set; }
    public Func<BackgroundTaskInfo, IRenderable>? TaskPreview { get; set; }
    public Action? RefreshDetails { get; set; }
    /// <summary>Cheap change signal (BackgroundTasks.Revision). When it moved, task state is rescanned immediately.</summary>
    public Func<long>? TasksRevision { get; set; }
    public int Version
    {
        get
        {
            // With a change signal (BackgroundTasks.Revision) unchanged frames are O(1); without one the
            // scan runs every time, as before. Start/Complete always force a rescan.
            var now = Environment.TickCount64;
            var revision = TasksRevision?.Invoke();
            if (revision is { } signal && signal == _taskRevision && now - _taskScanAt < 250) return Volatile.Read(ref _version);
            _taskScanAt = now;
            if (revision is { } current) _taskRevision = current;
            _taskMemo.Clear();
            RefreshDetails?.Invoke();
            var hash = new HashCode();
            foreach (var call in Invocations) foreach (var task in TasksFor(call)) { hash.Add(task.Id); hash.Add(task.UpdatedAt); hash.Add(task.State); }
            var result = hash.ToHashCode();
            if (result != _taskVersion) { _taskVersion = result; _content = null; Interlocked.Increment(ref _version); }
            return Volatile.Read(ref _version);
        }
    }
    private long _taskScanAt;
    private long _taskRevision = -1;
    private readonly Dictionary<string, IReadOnlyList<BackgroundTaskInfo>> _taskMemo = new(StringComparer.Ordinal);
    public long AnimationVersion => !IsReasoning && (Ok == null || Invocations.Any(call => TasksFor(call).Any(task => BackgroundTasks.IsActive(task.State)))) ? Environment.TickCount64 / 80 : 0;
    private bool _useMarkers;
    public bool UseMarkers
    {
        get => _useMarkers;
        set
        {
            if (_useMarkers == value) return;
            _useMarkers = value;
            _content = null;
            _taskScanAt = 0;
            Interlocked.Increment(ref _version);
        }
    }
    public IReadOnlyDictionary<int, Color> Markers => _markers;
    internal (int Span, int Selected) InspectionTarget(int marker) => _childTargets.TryGetValue(marker, out var target) ? target : (marker, 0);
    private int ChildMarker(ToolInvocation call, BackgroundTaskInfo task)
    {
        var key = call.Id + ":" + task.Id;
        if (!_childMarkers.TryGetValue(key, out var marker)) _childMarkers[key] = marker = Spans.Count + _childMarkers.Count;
        _childTargets[marker] = (call.SpanIndex, DetailEntries(call.SpanIndex).FindIndex(entry => entry.Call == call && entry.Task?.Id == task.Id));
        return marker;
    }
    public bool IsReasoning { get; } = reasoning;
    public bool IsChildTranscript { get; set; }
    public string Id { get; } = id;
    public string TraceId { get; } = Guid.NewGuid().ToString("N");
    public string Code { get; } = TerminalSafeText.Clean(code.Replace("\r\n", "\n").Replace('\r', '\n'));
    public IReadOnlyList<PythonToolSpan> Spans { get; } = PythonToolSpans.Parse(TerminalSafeText.Clean(code.Replace("\r\n", "\n").Replace('\r', '\n')));
    public List<ToolInvocation> Invocations { get; } = [];
    public string? Result { get; private set; }
    public bool? Ok { get; private set; }
    public Style TraceStyle(int span, Color foreground, Decoration decoration = Decoration.None)
    {
        if (!UseMarkers) return new Style(foreground, decoration: decoration);
        if (!_markers.TryGetValue(span, out var marker))
        {
            var id = Interlocked.Increment(ref _nextMarker);
            marker = new Color((byte)(id >> 16), (byte)(id >> 8), (byte)id); _markers.Add(span, marker);
        }
        return new Style(foreground, marker, decoration);
    }
    public void Start(ToolCallStarted call)
    {
        if (Invocations.Any(invocation => invocation.Id == call.Id)) return;
        var candidates = Spans.Select((span, index) => (span, index)).Where(pair => pair.span.Name == call.ToolName).ToArray();
        var index = candidates.FirstOrDefault(pair => Invocations.All(invocation => invocation.SpanIndex != pair.index), (null!, -1)).index;
        if (index < 0 && candidates.Length > 0) index = candidates[^1].index;
        Invocations.Add(new(call.Id, call.ToolName, call.ArgsJson, index));
        _content = null; _taskScanAt = 0; _taskRevision = -1; Interlocked.Increment(ref _version);
    }
    public void Complete(ToolCallCompleted call)
    {
        using var json = JsonDocument.Parse(call.ResultJson);
        var ok = json.RootElement.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (call.Id == Id) { Result = call.ResultJson; Ok = ok; }
        else if (Invocations.Find(invocation => invocation.Id == call.Id) is { } invocation) { invocation.Result = call.ResultJson; invocation.Ok = ok; }
        _content = null; _taskScanAt = 0; _taskRevision = -1; Interlocked.Increment(ref _version);
    }
    public IRenderable Details(int span, int selected)
    {
        RefreshDetails?.Invoke();
        if (IsReasoning) return new Rows(new Text(L.Pick("模型返回的思考内容", "Reasoning returned by the model"), new Style(Color.Grey)), new Text(Code));
        var entries = DetailEntries(span);
        var calls = span < 0 ? Invocations : Invocations.Where(invocation => invocation.SpanIndex == span).ToList();
        var rows = new List<IRenderable>();
        if (span < 0) rows.Add(new Rows(new Text(L.Pick("调度代码（Python）", "Dispatch code (Python)"), new Style(Color.Grey)), new PythonCodeText(this)));
        if (span < 0 && calls.Count > 0 && Result != null) rows.Add(new Text(L.Pick("Python 执行结果\n", "Python result\n") + Pretty(Result)));
        if (calls.Count > 0)
        {
            var entry = entries[Math.Clamp(selected, 0, entries.Count - 1)];
            var call = entry.Call;
            rows.Add(new Text(call.Name + " · " + State(call.Ok) + $" · {Math.Clamp(selected, 0, calls.Count - 1) + 1}/{calls.Count}", new Style(call.Ok == false ? Color.Red : Color.Green)));
            if (entry.Task is { } task)
            {
                if (span >= 0 && IsChildTask(task) && TaskTranscript != null) return new Rows(new Text(L.Pick($"子代理 {Math.Clamp(selected, 0, entries.Count - 1) + 1}/{entries.Count} · ←→ 切换", $"Child agent {Math.Clamp(selected, 0, entries.Count - 1) + 1}/{entries.Count} · ←→ switch"), new Style(Color.Grey)), TaskTranscript(task));
                rows.Add(new Text(TerminalSafeText.Clean(task.Title + L.Pick("\n状态 · ", "\nState · ") + TaskState(task.State) + L.Pick("\n当前步骤 · ", "\nCurrent step · ") + (task.CurrentStep ?? L.Pick("等待执行", "Waiting to run")))));
                rows.Add(new Text(TerminalSafeText.Clean(string.Join('\n', task.Steps))));
                rows.Add(new Text(TerminalSafeText.Clean(L.Pick("输出 / 结果\n", "Output / result\n") + task.Output + (task.Error == null ? "" : L.Pick("\n错误 · ", "\nError · ") + task.Error))));
            }
            else if (Diff(call) is { } diff)
            {
                rows.Add(new Text(TerminalUi.ToolLabel(call.Name, call.Args)));
                rows.Add(DiffRows(diff, 120));
            }
            else
            {
                if (ToolCardPresentation.Read(call.Result) is { } detailCard)
                {
                    var detail = ToolCardPresentation.Detail(detailCard);
                    if (detail.Length > 0) rows.Add(new Text(L.Pick("卡片\n", "Card\n") + detail));
                }
                rows.Add(new Text(L.Pick("入参\n", "Arguments\n") + Pretty(call.Args)));
                rows.Add(new Text(L.Pick("结果\n", "Result\n") + (call.Result == null ? L.Pick("正在等待执行结果 / 用户授权", "Waiting for the result / user approval") : Pretty(call.Result))));
            }
        }
        else
        {
            rows.Add(new Text(span >= 0 ? Spans[span].Name + L.Pick(" · 尚未执行此调用", " · this call has not run yet") : "Python · " + State(Ok)));
            if (span < 0) rows.Add(new Text(L.Pick("执行结果\n", "Execution result\n") + (Result == null ? L.Pick("等待沙箱返回", "Waiting for the sandbox") : Pretty(Result))));
        }
        return new Rows(rows);
    }
    internal BackgroundTaskInfo? SelectedTask(int span, int selected)
    {
        var entries = DetailEntries(span);
        return span < 0 || entries.Count == 0 ? null : entries[Math.Clamp(selected, 0, entries.Count - 1)].Task;
    }
    internal int DetailCount(int span) => DetailEntries(span).Count;
    private List<(ToolInvocation Call, BackgroundTaskInfo? Task)> DetailEntries(int span)
    {
        var entries = new List<(ToolInvocation, BackgroundTaskInfo?)>();
        foreach (var call in Invocations.Where(call => span < 0 || call.SpanIndex == span))
        {
            var tasks = TasksFor(call);
            if (tasks.Count == 0) entries.Add((call, null));
            else foreach (var task in tasks) entries.Add((call, task));
        }
        return entries;
    }
    private IReadOnlyList<BackgroundTaskInfo> TasksFor(ToolInvocation call)
    {
        if (_taskMemo.TryGetValue(call.Id, out var memoized)) return memoized;
        var result = ComputeTasks(call); _taskMemo[call.Id] = result; return result;
    }
    private IReadOnlyList<BackgroundTaskInfo> ComputeTasks(ToolInvocation call)
    {
        if (InvocationTasks?.Invoke(call.Id) is { Count: > 0 } live) return live;
        var tasks = new List<BackgroundTaskInfo>();
        if (call.Result != null && TaskLookup != null)
        {
            using var json = JsonDocument.Parse(call.Result);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var result in results.EnumerateArray())
                    if (result.TryGetProperty("taskId", out var id) && id.ValueKind == JsonValueKind.String && Lookup(id.GetString()!) is { } task) tasks.Add(task);
        }
        if (tasks.Count == 0 && TaskInfo(call) is { } single) tasks.Add(single);
        return tasks;
    }
    private BackgroundTaskInfo? Lookup(string id)
    {
        try { return TaskLookup?.Invoke(id); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }
    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static string State(bool? ok) => ok == true ? L.Pick("成功", "Succeeded") : ok == false ? L.Pick("失败", "Failed") : L.Pick("执行中", "Running");
    private static string TaskState(BackgroundTaskState state) => state switch
    {
        BackgroundTaskState.Queued => L.Pick("排队中", "Queued"), BackgroundTaskState.Running => L.Pick("运行中", "Running"), BackgroundTaskState.Completed => L.Pick("已完成", "Completed"),
        BackgroundTaskState.AwaitingApproval => L.Pick("等待授权", "Awaiting approval"), BackgroundTaskState.AwaitingAnswer => L.Pick("等待回答", "Awaiting answer"),
        BackgroundTaskState.Cancelling => L.Pick("正在停止", "Stopping"),
        BackgroundTaskState.Cancelled => L.Pick("已停止", "Stopped"), BackgroundTaskState.Interrupted => L.Pick("已中断", "Interrupted"), _ => L.Pick("失败", "Failed")
    };
    private static bool IsChildTask(BackgroundTaskInfo task) => task.Kind == "agent" || task.Kind != "shell" && task.ParentSessionId != null;
    private BackgroundTaskInfo? TaskInfo(ToolInvocation call)
    {
        try
        {
            if (TaskLookup == null || call.Result == null) return null;
            using var json = JsonDocument.Parse(call.Result);
            string? id = null;
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("taskId", out var taskId)) id = taskId.GetString();
            if (id == null && call.Name is "cancel_agent" or "shell_status" or "cancel_shell")
            { using var args = JsonDocument.Parse(call.Args); if (args.RootElement.TryGetProperty("id", out var selector)) id = selector.GetString(); }
            return id == null ? null : TaskLookup(id);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }
    private static JsonElement? Diff(ToolInvocation call)
    {
        if (call.Result == null) return null;
        using var json = JsonDocument.Parse(call.Result);
        return json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("diff", out var diff) ? diff.Clone() : null;
    }
    private IRenderable DiffRows(JsonElement diff, int limit, int span = -1)
    {
        var rows = new List<IRenderable> { new Text(L.Pick($"+{diff.GetProperty("added").GetInt32()} / −{diff.GetProperty("removed").GetInt32()} 行", $"+{diff.GetProperty("added").GetInt32()} / −{diff.GetProperty("removed").GetInt32()} lines"), TraceStyle(span, Color.Grey)) };
        var lines = diff.GetProperty("lines").EnumerateArray().ToArray();
        foreach (var line in lines.Take(limit))
        {
            var kind = line.GetProperty("kind").GetString();
            var number = kind == "removed" ? line.GetProperty("oldLine") : line.GetProperty("newLine");
            rows.Add(new Text($"{number.ToString(),4} {(kind == "added" ? "+" : kind == "removed" ? "−" : " ")} {TerminalSafeText.Clean(line.GetProperty("text").GetString() ?? "")}", UseMarkers ? TraceStyle(span, kind == "added" ? Color.Green : kind == "removed" ? Color.Red : Color.Grey) : new Style(kind == "added" ? Color.Green : kind == "removed" ? Color.Red : Color.Grey, kind == "added" ? new Color(0, 65, 49) : kind == "removed" ? new Color(85, 0, 22) : Color.Default)));
        }
        if (lines.Length > limit) rows.Add(new Text(L.Pick("… 点击查看完整变更（详情可滚动）", "… click for the full diff (the detail view scrolls)"), TraceStyle(span, Color.Grey)));
        if (diff.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) rows.Add(new Text(L.Pick("变更较长，记录已截断。", "The change is large; the record was truncated."), TraceStyle(span, Color.Grey)));
        return new Rows(rows);
    }
    internal static string ToolBadge(string name) => name switch
    {
        "everything_search" => "EVERYTHING", "find" => "FIND", "file_read" or "read" => "READ", "file_edit" or "edit" => "EDIT", "file_write" or "write" => "WRITE", "file_search" or "grep" or "web_search" => "SEARCH",
        "run_shell" or "bash" or "powershell" => "SHELL", "list_directory" or "ls" => "LIST", "subagent" or "cancel_agent" => "AGENT", _ => name.ToUpperInvariant()
    };
    internal static string ToolSubject(string name, string argsJson, string fallback)
    {
        if (name == "browser") return BrowserPresentation.Title(argsJson);
        using var doc = JsonDocument.Parse(argsJson); var args = doc.RootElement;
        string Get(string key, string defaultValue = "") => args.TryGetProperty(key, out var v) ? v.ToString() : defaultValue;
        if (name == "everything_search") return "\"" + Get("query") + "\" in " + Get("path", ".");
        if (name is "file_search" or "grep" or "find")
        {
            var pattern = Get("pattern");
            if (pattern.Length == 0) pattern = Get("file_pattern", "*");
            return "\"" + pattern + "\" in " + Get("path", ".");
        }
        if (name == "web_search") return "\"" + Get("query") + "\"";
        if (name is "bash" or "powershell") return Get("command");
        return name is "file_read" or "file_edit" or "file_write" or "list_directory" or "read" or "write" or "edit" or "ls" ? Get("path", ".") : fallback;
    }
    private static string[] OutputLines(ToolInvocation call, string fallback)
    {
        if (call.Result == null) return [fallback];
        using var doc = JsonDocument.Parse(call.Result);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [fallback];
        if (data.TryGetProperty("paths", out var paths)) return new[] { "Found " + paths.GetArrayLength() + " files" }.Concat(paths.EnumerateArray().Select(p => p.GetString() ?? "")).ToArray();
        if (data.TryGetProperty("files", out var files)) return new[] { "Found " + files.GetArrayLength() + " files" }.Concat(files.EnumerateArray().Select(p => p.GetString() ?? "")).ToArray();
        if (data.TryGetProperty("matches", out var matches)) return matches.EnumerateArray().Select(m => $"{m.GetProperty("path")}:{m.GetProperty("line")}: {m.GetProperty("text")}").DefaultIfEmpty("No matches").ToArray();
        if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return (content.GetString() ?? "").TrimEnd().Split('\n');
        if (data.TryGetProperty("stdout", out var stdout)) return ((data.TryGetProperty("exitCode", out var exit) ? "Exit code: " + exit + "\n" : "") + stdout.GetString() + (data.TryGetProperty("stderr", out var stderr) ? "\n" + stderr.GetString() : "")).TrimEnd().Split('\n');
        return [fallback];
    }
    private IRenderable Content() => _content ??= BuildContent();
    internal IRenderable LatestToolPreview()
    {
        var call = Invocations.Last();
        var card = ToolCardPresentation.Read(call.Result);
        var labelText = card != null
            ? ToolCardPresentation.Badge(card, call.Name) + " · " + ToolCardPresentation.Title(card, TerminalUi.ToolLabel(call.Name, call.Args))
            : ToolBadge(call.Name) + " · " + TerminalUi.ToolLabel(call.Name, call.Args);
        var rows = new List<IRenderable> { new Text(TerminalSafeText.Clean(labelText), new Style(Color.Grey)) };
        if (card != null)
        {
            foreach (var line in ToolCardPresentation.Lines(card).Take(4))
                rows.Add(new Text(TerminalSafeText.Clean(line), new Style(call.Ok == false ? Color.Red : Color.Grey)));
            var fields = ToolCardPresentation.Fields(card);
            if (fields.Length > 0) rows.Add(new Text(TerminalSafeText.Clean(fields), new Style(Color.Grey)));
        }
        else if (Diff(call) is { } diff) rows.Add(DiffRows(diff, 4));
        else foreach (var line in OutputLines(call, call.Result == null ? L.Pick("正在执行…", "Running…") : TerminalUi.ResultSummary(JsonSerializer.Deserialize<JsonElement>(call.Result), false, call.Name)).Take(4))
            rows.Add(new Text(TerminalSafeText.Clean(line), new Style(call.Ok == false ? Color.Red : Color.Grey)));
        return new Rows(rows);
    }
    private IRenderable BuildContent()
    {
        var rows = new List<IRenderable>();
        if (IsReasoning) { rows.Add(new MarkedThinkingLine(this, L.Pick("✳ 思考 ", "✳ Thinking "))); return new Rows(rows); }
        if (Invocations.Count == 0) rows.Add(new Text(Ok == null ? L.Pick("正在计算或准备操作…", "Computing or preparing…") : L.Pick("计算与处理已结束 · Ctrl+O 查看调度代码与结果", "Computation finished · Ctrl+O shows dispatch code and results"), TraceStyle(-1, Color.Grey)));
        if (Invocations.Count == 0 && Ok == null) rows.Add(new ToolRunningLine(L.Pick("正在执行工具…", "Running tools…")));
        foreach (var (call, index) in Invocations.Select((call, index) => (call, index)))
        {
            var colour = call.Ok == false ? Color.Red : call.Ok == true ? Color.Green : Color.Cyan1;
            var isComputer = call.Name.StartsWith("computer_", StringComparison.Ordinal);
            var card = ToolCardPresentation.Read(call.Result);
            var label = isComputer ? ComputerUsePresentation.Title(call.Name, call.Args, call.Result) : TerminalUi.ToolLabel(call.Name, call.Args);
            var preview = L.Pick("等待执行结果 / 用户授权", "Waiting for the result / user approval");
            if (call.Result != null)
            {
                using var result = JsonDocument.Parse(call.Result);
                preview = call.Ok == false ? result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? L.Pick("操作失败", "Operation failed") : L.Pick("操作失败", "Operation failed") : TerminalUi.ResultSummary(result.RootElement, false, call.Name);
            }
            var linkedTasks = TasksFor(call);
            var task = linkedTasks.FirstOrDefault();
            if (call.Name is "shell_status")
            {
                var status = call.Ok == false ? L.Pick("查询失败 · ", "Query failed · ") + preview : task == null ? State(call.Ok) : TaskState(task.State);
                rows.Add(new Text(L.Pick($"{index + 1}. 查询已有任务 {task?.Id[..Math.Min(8, task.Id.Length)] ?? ""} · {status}", $"{index + 1}. Existing task {task?.Id[..Math.Min(8, task.Id.Length)] ?? ""} · {status}"), TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                if (call.Ok == null) rows.Add(new ToolRunningLine());
                continue;
            }
            if (task != null) colour = task.State is BackgroundTaskState.Failed or BackgroundTaskState.Interrupted ? Color.Red : task.State == BackgroundTaskState.Completed ? Color.Green : Color.Cyan1;
            var badge = card != null ? ToolCardPresentation.Badge(card, call.Name)
                : isComputer ? L.Pick("电脑操作", "COMPUTER") : call.Name == "browser" ? BrowserPresentation.Badge(call.Args) : ToolBadge(call.Name);
            var badgeColour = card != null ? "#2d5a6e" : call.Name != "browser" ? "#5943ad" : badge is "PAGE" or "FIND" or "SHOT" ? "#275d8c" : badge is "TYPE" ? "#26705c" : "#875122";
            var subject = card != null ? ToolCardPresentation.Title(card, label) : TerminalSafeText.Clean(ToolSubject(call.Name, call.Args, label));
            var statusLabel = call.Ok == null ? task == null ? L.Pick("运行中", "Running") : TaskState(task.State) : call.Ok == true ? L.Pick("成功", "Succeeded") : L.Pick("失败", "Failed");
            var tint = $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";
            // CommandCode 风格：一行徽标 + 标题 + 内联状态，正文树形缩进，折叠提示只在末行。
            rows.Add(new Markup($"[bold white on {badgeColour}] {Markup.Escape(badge)} [/] [bold]{Markup.Escape(subject)}[/]  [{tint}]{statusLabel}[/]", TraceStyle(call.SpanIndex, Color.Default)));
            if (task != null)
            {
                foreach (var linked in linkedTasks)
                {
                    var marker = ChildMarker(call, linked);
                    var active = BackgroundTasks.IsActive(linked.State);
                    var childColour = linked.State is BackgroundTaskState.Failed or BackgroundTaskState.Interrupted ? Color.Red : active ? Color.Cyan1 : Color.Green;
                    var childRows = new List<IRenderable>
                    {
                        new AgentSummaryLine((IsChildTask(linked) ? L.Pick("子代理任务 · ", "Child task · ") : "") + linked.Id[..Math.Min(8, linked.Id.Length)] + " · " + TaskState(linked.State), new Style(childColour)),
                        new AgentSummaryLine(linked.Title, new Style(Color.Default))
                    };
                    var progress = BackgroundTasks.IsActive(linked.State) ? linked.CurrentStep ?? TaskState(linked.State) : TaskState(linked.State);
                    if (active) childRows.Add(new AgentSummaryLine(progress, new Style(Color.Grey)));
                    if (TaskPreview != null && IsChildTask(linked)) childRows.Add(new TextViewport(TaskPreview(linked), active ? 4 : 3, 0));
                    childRows.Add(new Text(L.Pick("点击 / Ctrl+O 查看完整执行消息与结果", "Click / Ctrl+O for the full execution messages and results"), new Style(Color.Grey)));
                    rows.Add(new MarkedTaskPreview(this, marker, new Panel(new Rows(childRows))
                        .Border(BoxBorder.Rounded).BorderStyle(new Style(childColour)).Expand()));
                    rows.Add(new Text(""));
                }
            }
            else if (card == null && call.Name is "todo_read" or "todo_write" && call.Ok == true)
                rows.Add(new Text("  │ " + TerminalSafeText.Clean(preview), TraceStyle(call.SpanIndex, Color.Default)));
            else if (card == null && Diff(call) is { } diff) rows.Add(new TextViewport(DiffRows(diff, 8, call.SpanIndex), 10, 0));
            else if (card != null)
            {
                var lines = ToolCardPresentation.Lines(card);
                if (lines.Length == 0) lines = [preview];
                foreach (var line in lines.Take(3)) rows.Add(new Text("  │ " + TerminalSafeText.Clean(line), TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                if (lines.Length > 3) rows.Add(new Text(L.Pick($"  …还有 {lines.Length - 3} 行 [Ctrl+O 展开]", $"  …{lines.Length - 3} more lines [Ctrl+O expand]"), TraceStyle(call.SpanIndex, Color.Grey)));
                var fields = ToolCardPresentation.Fields(card);
                if (fields.Length > 0) rows.Add(new Text("  │ " + TerminalSafeText.Clean(fields), TraceStyle(call.SpanIndex, Color.Grey)));
            }
            else
            {
                var output = OutputLines(call, preview);
                foreach (var line in output.Take(3)) rows.Add(new Text("  │ " + TerminalSafeText.Clean(line), TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                if (output.Length > 3) rows.Add(new Text(L.Pick($"  …还有 {output.Length - 3} 行 [Ctrl+O 展开]", $"  …{output.Length - 3} more lines [Ctrl+O expand]"), TraceStyle(call.SpanIndex, Color.Grey)));
                if (call.Ok == false && output.Length <= 3 && preview.Length > 0 && !preview.Contains('\n') && output.All(line => !line.Contains(preview)))
                    rows.Add(new Text("  │ " + TerminalSafeText.Clean(ToolResult.Clip(preview, 200)), TraceStyle(call.SpanIndex, Color.Red)));
            }
            if (task == null && call.Ok == null) rows.Add(new ToolRunningLine());
            rows.Add(new Text(""));
        }
        if (Ok == false && Result != null)
        {
            using var result = JsonDocument.Parse(Result);
            var message = result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? L.Pick("Python 执行失败", "Python execution failed") : L.Pick("Python 执行失败", "Python execution failed");
            var lines = TerminalSafeText.Clean(message).Split('\n');
            rows.Add(TerminalUi.ToolFailure(string.Join('\n', lines.Take(3))));
            if (lines.Length > 3) rows.Add(new Text(L.Pick($"… +{lines.Length - 3} lines [Ctrl+O 展开完整错误]", $"… +{lines.Length - 3} lines [Ctrl+O expand the full error]"), TraceStyle(-1, Color.Grey)));
        }

        var failures = Invocations.Count(call => call.Ok == false);
        if (Result != null)
        {
            using var json = JsonDocument.Parse(Result);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("hostCallSummary", out var summary) && summary.TryGetProperty("failed", out var failed) && failed.TryGetInt32(out var count)) failures = Math.Max(failures, count);
        }
        if (failures > 0) rows.Add(new Text(L.Pick($"含 {failures} 项操作失败", $"{failures} operations failed"), new Style(Color.Red)));
        return new Rows(rows);
    }
    public Measurement Measure(RenderOptions options, int maxWidth) => Content().Measure(options, maxWidth);
    internal bool DelegationFinished
    {
        get
        {
            var children = Invocations.Where(call => call.Name is "subagent").SelectMany(TasksFor).ToArray();
            return children.Length > 0 && children.All(task => !BackgroundTasks.IsActive(task.State));
        }
    }
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content().Render(options, maxWidth);
}

/// <summary>Keep long delegation prompts from consuming the entire batch preview.</summary>
internal sealed class AgentSummaryLine(string text, Style style) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var line = new Segment(TerminalSafeText.Clean(text).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), style);
        if (line.CellCount() <= maxWidth) return [line];
        return Segment.Truncate([line], Math.Max(0, maxWidth - 1)).Append(new Segment("…", style));
    }
}

internal sealed class MarkedTaskPreview(CodeExecutionBlock block, int span, IRenderable content) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => content.Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => content.Render(options, maxWidth).Select(segment =>
        block.UseMarkers && !segment.IsLineBreak
            ? new Segment(segment.Text, new Style(segment.Style.Foreground, block.TraceStyle(span, Color.Default).Background, segment.Style.Decoration))
            : segment);
}

internal sealed class PythonCodeText(CodeExecutionBlock execution) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(10, maxWidth), maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var offset = 0; var lineNumber = 1;
        foreach (var line in execution.Code.Split('\n'))
        {
            yield return new Segment($"{lineNumber++,3} │ ", execution.TraceStyle(-1, Color.Grey));
            var cursor = 0;
            while (cursor < line.Length)
            {
                var spanIndex = -1;
                for (var i = 0; i < execution.Spans.Count; i++) if (execution.Spans[i].Start <= offset + cursor && offset + cursor < execution.Spans[i].End) { spanIndex = i; break; }
                var end = spanIndex >= 0 ? Math.Min(line.Length, execution.Spans[spanIndex].End - offset) : execution.Spans.Where(span => span.Start > offset + cursor).Select(span => Math.Min(line.Length, span.Start - offset)).DefaultIfEmpty(line.Length).Min();
                yield return new Segment(line[cursor..end].Replace("\t", "    "), execution.TraceStyle(spanIndex, spanIndex >= 0 ? Color.Green : Color.Default, spanIndex >= 0 ? Decoration.Underline : Decoration.None));
                cursor = end;
            }
            offset += line.Length + 1;
            yield return Segment.LineBreak;
        }
    }
}

internal sealed class ToolRunningLine(string label = "") : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Segment.Truncate(
        [new Segment("  " + LoadingIndicator.DotsFrame(Environment.TickCount64) + " " + (label.Length > 0 ? label : L.Pick("正在执行工具…", "Running tools…")), new Style(Color.Cyan1))], maxWidth);
}

internal sealed class MarkedThinkingLine(CodeExecutionBlock block, string prefix) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        new ThinkingLine(block.Code, 0, prefix, L.Pick(" [Ctrl+O 展开]", " [Ctrl+O expand]")).Render(options, maxWidth)
            .Select(segment => new Segment(segment.Text, block.TraceStyle(-1, Color.Grey)));
}
