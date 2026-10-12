using System.Text.Json;
using LanAgent.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

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

internal sealed class CodeExecutionBlock(string id, string code) : IRenderable, IVersionedRenderable
{
    private static int _nextMarker;
    private readonly Dictionary<int, Color> _markers = [];
    private IRenderable? _content;
    private int _version;
    private int _taskVersion;
    public Func<string, BackgroundTaskInfo?>? TaskLookup { get; set; }
    public Func<BackgroundTaskInfo, IRenderable>? TaskTranscript { get; set; }
    public int Version
    {
        get
        {
            var hash = new HashCode();
            foreach (var call in Invocations) if (TaskInfo(call) is { } task) { hash.Add(task.UpdatedAt); hash.Add(task.State); }
            var current = hash.ToHashCode();
            if (current != _taskVersion) { _taskVersion = current; _content = null; Interlocked.Increment(ref _version); }
            return Volatile.Read(ref _version);
        }
    }
    public bool UseMarkers { get; set; }
    public IReadOnlyDictionary<int, Color> Markers => _markers;
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
        var candidates = Spans.Select((span, index) => (span, index)).Where(pair => pair.span.Name == call.ToolName).ToArray();
        var index = candidates.FirstOrDefault(pair => Invocations.All(invocation => invocation.SpanIndex != pair.index), (null!, -1)).index;
        if (index < 0 && candidates.Length > 0) index = candidates[^1].index;
        Invocations.Add(new(call.Id, call.ToolName, call.ArgsJson, index));
        _content = null; Interlocked.Increment(ref _version);
    }
    public void Complete(ToolCallCompleted call)
    {
        using var json = JsonDocument.Parse(call.ResultJson);
        var ok = json.RootElement.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (call.Id == Id) { Result = call.ResultJson; Ok = ok; }
        else if (Invocations.Find(invocation => invocation.Id == call.Id) is { } invocation) { invocation.Result = call.ResultJson; invocation.Ok = ok; }
        _content = null; Interlocked.Increment(ref _version);
    }
    public IRenderable Details(int span, int selected)
    {
        var calls = span < 0 ? Invocations : Invocations.Where(invocation => invocation.SpanIndex == span).ToList();
        var rows = new List<IRenderable>();
        if (span < 0) rows.Add(new Rows(new Text("调度代码（Python）", new Style(Color.Grey)), new PythonCodeText(this)));
        if (calls.Count > 0)
        {
            var call = calls[Math.Clamp(selected, 0, calls.Count - 1)];
            rows.Add(new Text(call.Name + " · " + State(call.Ok) + $" · {Math.Clamp(selected, 0, calls.Count - 1) + 1}/{calls.Count}", new Style(call.Ok == false ? Color.Red : Color.Green)));
            if (TaskInfo(call) is { } task)
            {
                if (IsChildTask(task) && TaskTranscript != null) return TaskTranscript(task);
                rows.Add(new Text(TerminalSafeText.Clean(task.Title + "\n状态 · " + TaskState(task.State) + "\n当前步骤 · " + (task.CurrentStep ?? "等待执行"))));
                rows.Add(new Text(TerminalSafeText.Clean(string.Join('\n', task.Steps))));
                rows.Add(new Text(TerminalSafeText.Clean("输出 / 结果\n" + task.Output + (task.Error == null ? "" : "\n错误 · " + task.Error))));
            }
            else if (Diff(call) is { } diff)
            {
                rows.Add(new Text(TerminalUi.ToolLabel(call.Name, call.Args)));
                rows.Add(DiffRows(diff, 120));
            }
            else
            {
                rows.Add(new Text("入参\n" + Pretty(call.Args)));
                rows.Add(new Text("结果\n" + (call.Result == null ? "正在等待执行结果 / 用户授权" : Pretty(call.Result))));
            }
        }
        else
        {
            rows.Add(new Text(span >= 0 ? Spans[span].Name + " · 尚未执行此调用" : "Python · " + State(Ok)));
            if (span < 0) rows.Add(new Text("执行结果\n" + (Result == null ? "等待沙箱返回" : Pretty(Result))));
        }
        return new Rows(rows);
    }
    internal BackgroundTaskInfo? SelectedTask(int span, int selected)
    {
        var calls = span < 0 ? Invocations : Invocations.Where(call => call.SpanIndex == span).ToList();
        return calls.Count == 0 ? null : TaskInfo(calls[Math.Clamp(selected, 0, calls.Count - 1)]);
    }
    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static string State(bool? ok) => ok == true ? "成功" : ok == false ? "失败" : "执行中";
    private static string TaskState(BackgroundTaskState state) => state switch
    {
        BackgroundTaskState.Queued => "排队中", BackgroundTaskState.Running => "运行中", BackgroundTaskState.Completed => "已完成",
        BackgroundTaskState.AwaitingApproval => "等待授权", BackgroundTaskState.AwaitingAnswer => "等待回答",
        BackgroundTaskState.Cancelled => "已停止", BackgroundTaskState.Interrupted => "已中断", _ => "失败"
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
            if (id == null && call.Name is "agent_status" or "wait_agent" or "cancel_agent" or "shell_status" or "cancel_shell")
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
        var rows = new List<IRenderable> { new Text($"+{diff.GetProperty("added").GetInt32()} / −{diff.GetProperty("removed").GetInt32()} 行", TraceStyle(span, Color.Grey)) };
        var lines = diff.GetProperty("lines").EnumerateArray().ToArray();
        foreach (var line in lines.Take(limit))
        {
            var kind = line.GetProperty("kind").GetString();
            var number = kind == "removed" ? line.GetProperty("oldLine") : line.GetProperty("newLine");
            rows.Add(new Text($"{number.ToString(),4} {(kind == "added" ? "+" : kind == "removed" ? "−" : " ")} {TerminalSafeText.Clean(line.GetProperty("text").GetString() ?? "")}", TraceStyle(span, kind == "added" ? Color.Green : kind == "removed" ? Color.Red : Color.Grey)));
        }
        if (lines.Length > limit) rows.Add(new Text("… 点击查看完整变更（详情可滚动）", TraceStyle(span, Color.Grey)));
        if (diff.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) rows.Add(new Text("变更较长，记录已截断。", TraceStyle(span, Color.Grey)));
        return new Rows(rows);
    }
    private IRenderable Content() => _content ??= BuildContent();
    private IRenderable BuildContent()
    {
        var rows = new List<IRenderable> { new ThinkingLine(Code, 0, "代码 › ") };
        if (Invocations.Count == 0) rows.Add(new Text(Ok == null ? "正在计算或准备操作…" : "计算与处理已结束 · 点击查看代码和结果", TraceStyle(-1, Color.Grey)));
        foreach (var (call, index) in Invocations.Select((call, index) => (call, index)))
        {
            var colour = call.Ok == false ? Color.Red : call.Ok == true ? Color.Green : Color.Cyan1;
            var label = TerminalUi.ToolLabel(call.Name, call.Args);
            var preview = "等待执行结果 / 用户授权";
            if (call.Result != null)
            {
                using var result = JsonDocument.Parse(call.Result);
                preview = call.Ok == false ? result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "操作失败" : "操作失败" : TerminalUi.ResultSummary(result.RootElement, false);
            }
            var task = TaskInfo(call);
            if (call.Name is "shell_status" or "agent_status" or "wait_agent")
            {
                var status = call.Ok == false ? "查询失败 · " + preview : task == null ? State(call.Ok) : TaskState(task.State);
                rows.Add(new Text($"{index + 1}. 查询已有任务 {task?.Id[..Math.Min(8, task.Id.Length)] ?? ""} · {status}", TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                continue;
            }
            if (task != null) colour = task.State is BackgroundTaskState.Failed or BackgroundTaskState.Interrupted ? Color.Red : task.State == BackgroundTaskState.Completed ? Color.Green : Color.Cyan1;
            var body = new List<IRenderable> { new Text($"{index + 1}. {label} · {(task == null ? State(call.Ok) : TaskState(task.State))}", TraceStyle(call.SpanIndex, colour)) };
            if (task != null)
            {
                body.Add(new Text(TerminalSafeText.Clean(task.Title), TraceStyle(call.SpanIndex, Color.Default)));
                var progress = task.CurrentStep ?? (task.State == BackgroundTaskState.Queued ? "等待执行" : TaskState(task.State));
                body.Add(new Text(TerminalSafeText.Clean(progress) + " · 点击查看进度与结果", TraceStyle(call.SpanIndex, Color.Grey)));
            }
            else if (Diff(call) is { } diff) body.Add(new TextViewport(DiffRows(diff, 8, call.SpanIndex), 10, 0));
            else body.Add(new Text(TerminalSafeText.Clean(preview), TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
            var panel = new Panel(new Rows(body)).Border(BoxBorder.Rounded).BorderColor(task?.Kind == "agent" ? Color.Cyan1 : colour).Padding(1, 0).Expand();
            if (task != null && IsChildTask(task)) panel.Header("子代理任务");
            else if (task?.Kind == "shell") panel.Header("后台命令");
            rows.Add(panel);
        }
        if (Ok == false && Result != null)
        {
            using var result = JsonDocument.Parse(Result);
            rows.Add(TerminalUi.ToolFailure(result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "Python 执行失败" : "Python 执行失败"));
        }
        rows.Add(new Text("点击操作查看详情 · Ctrl+E 查看调度代码与结果", TraceStyle(-1, Color.Grey)));
        var heading = Invocations.Count > 0 && Invocations.All(call => call.Name is "shell_status" or "agent_status" or "wait_agent") ? "任务查询" : "执行步骤";
        return new Panel(new Rows(rows)).Header(heading + " · " + State(Ok)).Border(BoxBorder.Rounded).BorderColor(Ok == false ? Color.Red : Color.Grey).Padding(1, 0).Expand();
    }
    public Measurement Measure(RenderOptions options, int maxWidth) => Content().Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content().Render(options, maxWidth);
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
