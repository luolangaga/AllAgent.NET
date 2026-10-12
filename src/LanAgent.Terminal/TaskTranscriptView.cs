using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>Child messages use the same Markdown, thought and CodeAct blocks as the main chat.</summary>
internal sealed class TaskTranscriptView(DataStore store, BackgroundTasks tasks, string id, Func<bool>? showThinking = null) : IRenderable, IVersionedRenderable
{
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
        var blocks = new Dictionary<string, CodeExecutionBlock>();
        var hostBlocks = new Dictionary<string, CodeExecutionBlock>();
        CodeExecutionBlock Create(string callId, string args)
        {
            using var json = JsonDocument.Parse(args);
            return new(callId, json.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "") { TaskLookup = key => tasks.Get(key) };
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
                        blocks[block.Id] = block; rows.Add(block);
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
                    if (!blocks.ContainsKey(start.Id)) { var block = Create(start.Id, start.ArgsJson); blocks[start.Id] = block; rows.Add(block); }
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
