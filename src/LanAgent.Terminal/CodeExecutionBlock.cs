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

internal sealed class CodeExecutionBlock(string id, string code, bool reasoning = false) : IRenderable, IVersionedRenderable
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
    public bool IsReasoning { get; } = reasoning;
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
        if (IsReasoning) return new Rows(new Text("模型返回的思考内容", new Style(Color.Grey)), new Text(Code));
        var calls = span < 0 ? Invocations : Invocations.Where(invocation => invocation.SpanIndex == span).ToList();
        var rows = new List<IRenderable>();
        if (span < 0) rows.Add(new Rows(new Text("调度代码（Python）", new Style(Color.Grey)), new PythonCodeText(this)));
        if (span < 0 && calls.Count > 0 && Result != null) rows.Add(new Text("Python 执行结果\n" + Pretty(Result)));
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
        BackgroundTaskState.Cancelling => "正在停止",
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
            rows.Add(new Text($"{number.ToString(),4} {(kind == "added" ? "+" : kind == "removed" ? "−" : " ")} {TerminalSafeText.Clean(line.GetProperty("text").GetString() ?? "")}", UseMarkers ? TraceStyle(span, kind == "added" ? Color.Green : kind == "removed" ? Color.Red : Color.Grey) : new Style(kind == "added" ? Color.Green : kind == "removed" ? Color.Red : Color.Grey, kind == "added" ? new Color(0, 65, 49) : kind == "removed" ? new Color(85, 0, 22) : Color.Default)));
        }
        if (lines.Length > limit) rows.Add(new Text("… 点击查看完整变更（详情可滚动）", TraceStyle(span, Color.Grey)));
        if (diff.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) rows.Add(new Text("变更较长，记录已截断。", TraceStyle(span, Color.Grey)));
        return new Rows(rows);
    }
    internal static string ToolBadge(string name) => name switch
    {
        "file_glob" or "find" => "GLOB", "file_read" or "read" => "READ", "file_edit" or "edit" => "EDIT", "file_write" or "write" => "WRITE", "file_search" or "grep" or "web_search" => "SEARCH",
        "run_shell" or "bash" or "powershell" => "SHELL", "list_directory" or "ls" => "LIST", "start_agent" or "subagent" => "AGENT", _ => name.ToUpperInvariant()
    };
    internal static string ToolSubject(string name, string argsJson, string fallback)
    {
        using var doc = JsonDocument.Parse(argsJson); var args = doc.RootElement;
        string Get(string key, string defaultValue = "") => args.TryGetProperty(key, out var v) ? v.ToString() : defaultValue;
        if (name is "file_search" or "file_glob" or "grep" or "find") return "\"" + Get("pattern") + "\" in " + Get("path", ".");
        if (name == "web_search") return "\"" + Get("query") + "\"";
        if (name is "bash" or "powershell") return Get("command");
        return name is "file_read" or "file_edit" or "file_write" or "list_directory" or "read" or "write" or "edit" or "ls" ? Get("path", ".") : fallback;
    }
    private static string[] OutputLines(ToolInvocation call, string fallback)
    {
        if (call.Result == null) return [fallback];
        using var doc = JsonDocument.Parse(call.Result);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [fallback];
        if (data.TryGetProperty("paths", out var paths)) return new[] { "Found " + data.GetProperty("totalMatches") + " files" }.Concat(paths.EnumerateArray().Select(p => p.GetString() ?? "")).ToArray();
        if (data.TryGetProperty("matches", out var matches)) return matches.EnumerateArray().Select(m => $"{m.GetProperty("path")}:{m.GetProperty("line")}: {m.GetProperty("text")}").DefaultIfEmpty("No matches").ToArray();
        if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return (content.GetString() ?? "").TrimEnd().Split('\n');
        if (data.TryGetProperty("stdout", out var stdout)) return ((data.TryGetProperty("exitCode", out var exit) ? "Exit code: " + exit + "\n" : "") + stdout.GetString() + (data.TryGetProperty("stderr", out var stderr) ? "\n" + stderr.GetString() : "")).TrimEnd().Split('\n');
        return [fallback];
    }
    private IRenderable Content() => _content ??= BuildContent();
    private IRenderable BuildContent()
    {
        var rows = new List<IRenderable> { new MarkedThinkingLine(this, IsReasoning ? "思考 › " : "代码 › ") };
        if (IsReasoning) return new Rows(rows);
        if (Invocations.Count == 0) rows.Add(new Text(Ok == null ? "正在计算或准备操作…" : "计算与处理已结束 · 点击查看代码和结果", TraceStyle(-1, Color.Grey)));
        foreach (var (call, index) in Invocations.Select((call, index) => (call, index)))
        {
            var colour = call.Ok == false ? Color.Red : call.Ok == true ? Color.Green : Color.Cyan1;
            var isComputer = call.Name.StartsWith("computer_", StringComparison.Ordinal);
            var label = isComputer ? ComputerUsePresentation.Title(call.Name, call.Args, call.Result) : TerminalUi.ToolLabel(call.Name, call.Args);
            var preview = "等待执行结果 / 用户授权";
            if (call.Result != null)
            {
                using var result = JsonDocument.Parse(call.Result);
                preview = call.Ok == false ? result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "操作失败" : "操作失败" : TerminalUi.ResultSummary(result.RootElement, false, call.Name);
            }
            var task = TaskInfo(call);
            if (call.Name is "shell_status" or "agent_status" or "wait_agent")
            {
                var status = call.Ok == false ? "查询失败 · " + preview : task == null ? State(call.Ok) : TaskState(task.State);
                rows.Add(new Text($"{index + 1}. 查询已有任务 {task?.Id[..Math.Min(8, task.Id.Length)] ?? ""} · {status}", TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                continue;
            }
            if (task != null) colour = task.State is BackgroundTaskState.Failed or BackgroundTaskState.Interrupted ? Color.Red : task.State == BackgroundTaskState.Completed ? Color.Green : Color.Cyan1;
            var body = new List<IRenderable> { new Markup($"[bold white on #5943ad] {ToolBadge(call.Name)} [/] [grey]{Markup.Escape("[" + TerminalSafeText.Clean(ToolSubject(call.Name, call.Args, label)) + "]")}[/]" , TraceStyle(call.SpanIndex, colour)) };
            if (task != null)
            {
                body.Add(new Text(TerminalSafeText.Clean(task.Title), TraceStyle(call.SpanIndex, Color.Default)));
                var progress = task.CurrentStep ?? (task.State == BackgroundTaskState.Queued ? "等待执行" : TaskState(task.State));
                body.Add(new Text(TerminalSafeText.Clean(progress) + " · 点击查看进度与结果", TraceStyle(call.SpanIndex, Color.Grey)));
            }
            else if (Diff(call) is { } diff) body.Add(new TextViewport(DiffRows(diff, 8, call.SpanIndex), 10, 0));
            else
            {
                var output = OutputLines(call, preview);
                foreach (var line in output.Take(3)) body.Add(new Text("  │ " + TerminalSafeText.Clean(line), TraceStyle(call.SpanIndex, call.Ok == false ? Color.Red : Color.Grey)));
                if (output.Length > 3) body.Add(new Text($"  └ … +{output.Length - 3} lines [点击 / Ctrl+E 展开]", TraceStyle(call.SpanIndex, Color.Grey)));
            }
            rows.Add(new Rows(body));
            rows.Add(new Text(""));
        }
        if (Ok == false && Result != null)
        {
            using var result = JsonDocument.Parse(Result);
            var message = result.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "Python 执行失败" : "Python 执行失败";
            var lines = TerminalSafeText.Clean(message).Split('\n');
            rows.Add(TerminalUi.ToolFailure(string.Join('\n', lines.Take(3))));
            if (lines.Length > 3) rows.Add(new Text($"… +{lines.Length - 3} lines [Ctrl+E 展开完整错误]", TraceStyle(-1, Color.Grey)));
        }
        rows.Add(new Text("点击操作查看详情 · Ctrl+E 查看调度代码与结果", TraceStyle(-1, Color.Grey)));

        var failures = Invocations.Count(call => call.Ok == false);
        if (Result != null)
        {
            using var json = JsonDocument.Parse(Result);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("hostCallSummary", out var summary) && summary.TryGetProperty("failed", out var failed) && failed.TryGetInt32(out var count)) failures = Math.Max(failures, count);
        }
        if (failures > 0) rows.Add(new Text($"含 {failures} 项操作失败", new Style(Color.Red)));
        return new Rows(rows);
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

internal sealed class MarkedThinkingLine(CodeExecutionBlock block, string prefix) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        new ThinkingLine(block.Code, 0, prefix, block.IsReasoning ? " [Ctrl+O 展开]" : " [Ctrl+E 展开]").Render(options, maxWidth)
            .Select(segment => new Segment(segment.Text, block.TraceStyle(-1, Color.Grey)));
}
