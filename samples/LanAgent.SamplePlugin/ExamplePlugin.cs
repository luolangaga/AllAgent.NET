using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;

namespace LanAgent.SamplePlugin;

public sealed class ExamplePlugin : ILanAgentPlugin
{
    public string Id => "lanagent.example";
    public string Name => "LanAgent 示例插件";
    private readonly SemaphoreSlim _audit = new(1, 1);
    public void Register(PluginContext context)
    {
        context.AddTools(new ExampleTools(context.DataDirectory));
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
        context.AddCommand(new("hello", "示例插件：显示问候", (args, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult("你好，" + (args.Length > 0 ? string.Join(' ', args) : "LanAgent 用户") + "！通用 DLL 插件正在工作。"); }));
        context.AddHook(new("example.audit", async (evt, ct) =>
        {
            await _audit.WaitAsync(ct);
            try { await File.AppendAllTextAsync(Path.Combine(context.DataDirectory, "lifecycle.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage = evt.Stage.ToString(), evt.SessionId, evt.ToolName, evt.Ok }) + "\n", ct); }
            finally { _audit.Release(); }
            return new HookDecision();
        }));
    }
    public ValueTask DisposeAsync() { _audit.Dispose(); return ValueTask.CompletedTask; }
}

public partial class ExampleTools(string directory)
{
    [AgentTool("example_clock"), Description("Read the current UTC clock from the example host plugin.")]
    public Task<ToolResult> ClockAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(ToolResult.Success(new { utc = DateTimeOffset.UtcNow })); }
    [AgentTool("example_note", Risk = ToolRisk.Write), Description("Store a bounded note in the example plugin data directory. Requires human approval.")]
    public async Task<ToolResult> NoteAsync([Description("Note text, maximum 4000 characters")] string text, CancellationToken ct = default)
    {
        if (text.Length > 4000) return ToolResult.Failure("Note exceeds 4000 characters.");
        await File.WriteAllTextAsync(Path.Combine(directory, "note.txt"), text, ct); return ToolResult.Success(new { saved = true, characters = text.Length });
    }
}

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
