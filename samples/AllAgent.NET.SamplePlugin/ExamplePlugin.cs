using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.SamplePlugin;

public sealed class ExamplePlugin : IAllAgentPlugin
{
    public string Id => "allagent.example";
    public string Name => "AllAgent 示例插件";
    private readonly SemaphoreSlim _audit = new(1, 1);
    private Timer? _ticker;
    public void Register(PluginContext context)
    {
        var tools = new ExampleTools(context.DataDirectory);
        context.AddTools(tools);
        context.AddTurnExtension(new("example.turn", (turn, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            turn.SystemPrompt += "\nThe example plugin provides example_clock for the current UTC time.";
            turn.Skills["example-review"] = new InstructionSkill("example-review", "示例插件提供的审查指引",
                "Read the relevant code and report concrete defects with file references. Respect host tool permissions.",
                "", "plugin", true, false, true);
            return ValueTask.CompletedTask;
        }));
        context.AddRequestExtension(new("example.request", (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            request.Messages.Insert(1, new ChatMessage("system", "The example plugin is active for this request."));
            return ValueTask.CompletedTask;
        }));
        context.AddProvider(new ExampleProvider());
        context.AddCommand(new("hello", "示例插件：显示问候", (args, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult("你好，" + (args.Length > 0 ? string.Join(' ', args) : "AllAgent 用户") + "！通用 DLL 插件正在工作。"); }));
        context.AddHook(new("example.audit", async (evt, ct) =>
        {
            await _audit.WaitAsync(ct);
            try { await File.AppendAllTextAsync(Path.Combine(context.DataDirectory, "lifecycle.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage = evt.Stage.ToString(), evt.SessionId, evt.ToolName, evt.Ok }) + "\n", ct); }
            finally { _audit.Release(); }
            return new HookDecision();
        }));
        // Web extensions: the dashboard page calls this backend method for the clock history, and
        // frames subscribed to "tick" receive a push event every five seconds.
        context.AddWebBridge<ClockHistoryQuery?, ClockHistoryResult>("history", (query, ct) =>
            tools.ReadHistoryAsync(Math.Clamp(query?.Limit ?? 20, 1, 100), ct));
        _ticker = new Timer(_ => { try { context.Web.Push("tick", new { utc = DateTimeOffset.UtcNow }); } catch { /* no web UI connected */ } }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }
    public ValueTask DisposeAsync() { _ticker?.Dispose(); _audit.Dispose(); return ValueTask.CompletedTask; }
}

public partial class ExampleTools(string directory)
{
    private readonly SemaphoreSlim _history = new(1, 1);
    private static readonly JsonSerializerOptions HistoryOptions = new(JsonSerializerDefaults.Web);

    [AgentTool("example_clock"), Description("Read the current UTC clock from the example host plugin.")]
    public async Task<ToolResult> ClockAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var utc = DateTimeOffset.UtcNow;
        try { await RecordAsync(utc, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* history is best effort */ }
        return ToolResult.Success(new { utc, source = "example_clock" });
    }

    [AgentTool("example_note", Risk = ToolRisk.Write), Description("Store a bounded note in the example plugin data directory. Requires human approval.")]
    public async Task<ToolResult> NoteAsync([Description("Note text, maximum 4000 characters")] string text, CancellationToken ct = default)
    {
        if (text.Length > 4000) return ToolResult.Failure("Note exceeds 4000 characters.");
        await File.WriteAllTextAsync(Path.Combine(directory, "note.txt"), text, ct); return ToolResult.Success(new { saved = true, characters = text.Length });
    }

    private async Task RecordAsync(DateTimeOffset utc, CancellationToken ct)
    {
        var entry = JsonSerializer.Serialize(new ClockHistoryEntry(utc.ToString("o"), DateTimeOffset.UtcNow.ToString("o")), HistoryOptions);
        await _history.WaitAsync(ct);
        try { await File.AppendAllTextAsync(Path.Combine(directory, "clock-history.jsonl"), entry + "\n", ct); }
        finally { _history.Release(); }
    }

    /// <summary>Latest clock calls, newest first; served to the plugin's own web frames.</summary>
    public async Task<ClockHistoryResult> ReadHistoryAsync(int limit, CancellationToken ct)
    {
        var path = Path.Combine(directory, "clock-history.jsonl");
        if (!File.Exists(path)) return new([], 0);
        var lines = await File.ReadAllLinesAsync(path, ct);
        var all = lines.Where(line => line.Length > 0)
            .Select(line => { try { return JsonSerializer.Deserialize<ClockHistoryEntry>(line, HistoryOptions); } catch (JsonException) { return null; } })
            .Where(entry => entry != null)
            .Cast<ClockHistoryEntry>()
            .ToArray();
        return new(all.Reverse().Take(limit).ToArray(), all.Length);
    }
}

public sealed record ClockHistoryQuery(int Limit = 20);
public sealed record ClockHistoryEntry(string Utc, string RecordedAt);
public sealed record ClockHistoryResult(IReadOnlyList<ClockHistoryEntry> Items, int Total);

/// <summary>Offline example provider for exercising plugin integration; it does not impersonate a remote model.</summary>
public sealed class ExampleProvider : IAiProvider
{
    public string ProviderId => "example";
    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(100, ct);
        yield return new(ChunkType.TextDelta, "示例插件模型已收到任务：" + ToolResult.Clip(request.Messages.Last().Content, 120));
        yield return new(ChunkType.Done, FinishReason: "stop");
    }
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("example-local", "示例插件模型（离线演示）")]);
}
