using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>Child messages use the same Markdown, thought and CodeAct blocks as the main chat.</summary>
internal sealed class TaskTranscriptView(DataStore store, BackgroundTasks tasks, string id, Func<bool>? showThinking = null, Action<CodeExecutionBlock>? registerExecution = null) : IRenderable, IVersionedRenderable
{
    private readonly Dictionary<string, CodeExecutionBlock> _blocks = [];
    private readonly Dictionary<string, CodeExecutionBlock> _hostBlocks = [];
    internal static IRenderable LatestStep(DataStore store, BackgroundTasks tasks, BackgroundTaskInfo task, bool showThinking)
    {
        if (!BackgroundTasks.IsActive(task.State) && task.FinalOutput is { Length: > 0 } final)
            return TerminalMarkdown.Render(final);
        var events = tasks.Activity(task.Id).Events;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ToolCallStarted { ToolName: not "execute_code" } start)
                return ToolStep(start, events.OfType<ToolCallCompleted>().LastOrDefault(done => done.Id == start.Id));
            if (events[i] is TokenDelta)
                return TerminalMarkdown.Render(string.Concat(events.Take(i + 1).OfType<TokenDelta>().Select(delta => delta.Text)));
            if (showThinking && events[i] is ThinkingDelta)
                return new ThinkingLine(string.Concat(events.Take(i + 1).OfType<ThinkingDelta>().Select(delta => delta.Text)), 0);
        }
        var messages = store.GetVisibleMessages(task.SessionId);
        foreach (var message in messages.AsEnumerable().Reverse())
        {
            if (message.Role == "assistant" && message.Content.Length > 0) return TerminalMarkdown.Render(message.Content);
            if (message.HostToolTraceJson is not { } traceJson) continue;
            try
            {
                var trace = JsonSerializer.Deserialize<HostToolTrace[]>(traceJson, ToolResult.JsonOptions)?.LastOrDefault();
                if (trace != null) return ToolStep(new(trace.Id, trace.ToolName, trace.ArgsJson), new(trace.Id, trace.ToolName, trace.ResultJson));
            }
            catch (JsonException) { }
        }
        return new Text(TerminalSafeText.Clean(task.Error ?? task.CurrentStep ?? "正在启动子代理…"), new Style(Color.Grey));
    }
    private static IRenderable ToolStep(ToolCallStarted start, ToolCallCompleted? done)
    {
        // Reuse the actual host-call renderer, omitting only its Python header/footer.
        var block = new CodeExecutionBlock(start.ParentId ?? start.Id, "");
        block.Start(start); if (done != null) block.Complete(done);
        return block.LatestToolPreview();
    }
    private IRenderable? _content;
    private int _version;
    public int Version
    {
        get
        {
            var task = tasks.Get(id);
            var hash = HashCode.Combine(tasks.Activity(id).Version, task.UpdatedAt, task.State, store.GetVisibleMessages(task.SessionId).Count, showThinking?.Invoke() ?? true);
            if (_version != hash) { _version = hash; _content = null; }
            return hash;
        }
    }
    private IRenderable Content()
    {
        _ = Version;
        if (_content != null) return _content;
        var task = tasks.Get(id);
        var messages = store.GetVisibleMessages(task.SessionId).Where(m => !m.IsInternal).ToArray();
        var rows = new List<IRenderable> { new Text(TerminalSafeText.Clean(task.Title + " · " + task.State), new Style(Color.Cyan1)) };
        var blocks = _blocks;
        var hostBlocks = _hostBlocks;
        var renderedBlocks = new HashSet<string>();
        CodeExecutionBlock Create(string callId, string args)
        {
            if (blocks.TryGetValue(callId, out var existing)) return existing;
            using var json = JsonDocument.Parse(args);
            var block = new CodeExecutionBlock(callId, json.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "") { TaskLookup = key => tasks.Get(key), IsChildTranscript = true };
            block.RefreshDetails = () => { _ = Content(); };
            if (registerExecution != null) registerExecution(block); else FullScreenTerminal.Current?.RegisterExecution(block);
            return block;
        }
        foreach (var message in messages)
        {
            if (message.Role is "user" or "assistant" && message.Content.Length > 0)
                rows.Add(TerminalMarkdown.Message(message.Role == "user" ? "你" : "LanAgent", message.Role == "user" ? SessionTimeline.Text(message) : message.Content, message.Role == "user"));
            if (message.Images.Count > 0) rows.Add(ImageAttachmentsView.Render(message.Images));
            if ((showThinking?.Invoke() ?? true) && message.ReasoningContent is { Length: > 0 } thinking) rows.Add(new ThinkingLine(thinking, 0));
            try
            {
                if (message.Role == "assistant" && message.ToolCallsJson != null)
                {
                    using var calls = JsonDocument.Parse(message.ToolCallsJson);
                    foreach (var call in calls.RootElement.EnumerateArray())
                    {
                        var function = call.GetProperty("function"); if (function.GetProperty("name").GetString() != "execute_code") continue;
                        var block = Create(call.GetProperty("id").GetString()!, function.GetProperty("arguments").GetString()!);
                        blocks[block.Id] = block; rows.Add(block); renderedBlocks.Add(block.Id);
                    }
                }
                if (message.Role == "tool" && message.ToolCallId is { } completedId && blocks.TryGetValue(completedId, out var completedBlock))
                {
                    completedBlock.Complete(new(completedId, "execute_code", message.Content));
                    foreach (var trace in JsonSerializer.Deserialize<HostToolTrace[]>(message.HostToolTraceJson ?? "[]", ToolResult.JsonOptions) ?? [])
                    { completedBlock.Start(new(trace.Id, trace.ToolName, trace.ArgsJson)); completedBlock.Complete(new(trace.Id, trace.ToolName, trace.ResultJson)); }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            { rows.Add(new Text("旧执行记录无法展开 · " + TerminalSafeText.Clean(ex.Message), new Style(Color.Grey))); }
        }
        var activity = tasks.Activity(id);
        var text = string.Concat(activity.Events.OfType<TokenDelta>().Select(e => e.Text));
        var thought = string.Concat(activity.Events.OfType<ThinkingDelta>().Select(e => e.Text));
        var lastAssistant = messages.LastOrDefault(m => m.Role == "assistant");
        if (text.Length > 0 && text != lastAssistant?.Content) rows.Add(TerminalMarkdown.Message("LanAgent", text, false));
        if ((showThinking?.Invoke() ?? true) && thought.Length > 0 && thought != lastAssistant?.ReasoningContent) rows.Add(new ThinkingLine(thought, 0));
        var storedTools = messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId).ToHashSet();
        foreach (var evt in activity.Events)
        {
            if (evt is ToolCallStarted start)
            {
                if (start.ToolName == "execute_code")
                {
                    var block = Create(start.Id, start.ArgsJson); blocks[start.Id] = block;
                    if (renderedBlocks.Add(start.Id)) rows.Add(block);
                }
                else if (start.ParentId is { } parent && !storedTools.Contains(parent) && blocks.TryGetValue(parent, out var block)) { block.Start(start); hostBlocks[start.Id] = block; }
            }
            else if (evt is ToolCallCompleted done && !storedTools.Contains(done.Id))
            {
                if (blocks.TryGetValue(done.Id, out var block)) block.Complete(done);
                else if (hostBlocks.TryGetValue(done.Id, out var host)) host.Complete(done);
            }
            else if (evt is AgentNotice notice) rows.Add(new Text("↻ " + TerminalSafeText.Clean(notice.Message), new Style(Color.Cyan1)));
        }
        if (messages.Length == 0 && activity.Events.Count == 0) rows.Add(new Text("正在启动子代理…", new Style(Color.Grey)));
        if (task.CurrentStep is { } step) rows.Add(new Text(TerminalSafeText.Clean(step), new Style(Color.Grey)));
        if (task.Error is { } error) rows.Add(TerminalUi.ToolFailure(error));
        return _content = new Rows(rows);
    }
    public Measurement Measure(RenderOptions options, int maxWidth) => Content().Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content().Render(options, maxWidth);
}
