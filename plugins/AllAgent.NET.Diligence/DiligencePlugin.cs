using System.Text.Json;
using AllAgent.NET.Core.Extensions;

namespace AllAgent.NET.Diligence;

/// <summary>Snapshots a persistent execution preference at the start of each turn.</summary>
public sealed class DiligencePlugin : IAllAgentPlugin
{
    public string Id => "allagent.diligence";
    public string Name => "Agent 执行积极度";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _mode = "normal";
    private string _settingsPath = "";

    public void Register(PluginContext context)
    {
        Directory.CreateDirectory(context.DataDirectory);
        _settingsPath = Path.Combine(context.DataDirectory, "effort.json");
        if (File.Exists(_settingsPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
                _mode = ParseMode(saved?.Mode ?? "")
                    ?? throw new InvalidDataException("积极度设置中的 Mode 无效，请修正或删除 effort.json 后重新启用插件。");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("积极度设置 effort.json 格式无效，请修正或删除后重新启用插件。", ex);
            }
        }

        context.AddCommand(new("effort", "查看或切换积极度：lazy / normal / diligent / off", ExecuteAsync));
        context.AddTurnExtension(new("allagent.diligence.policy", (turn, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var mode = Volatile.Read(ref _mode);
            if (mode != "off")
                turn.VolatileSystemPrompt += "\n\n<allagent-execution-effort mode=\"" + mode + "\">\n"
                    + CommonPolicy + "\n" + Policy(mode) + "\n</allagent-execution-effort>";
            return ValueTask.CompletedTask;
        }, Order: 100));
    }

    private async Task<string> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Length == 0 || (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase)))
            return "当前执行积极度：" + Label(Volatile.Read(ref _mode)) + "。\n" + Usage;
        if (args.Length != 1 || ParseMode(args[0]) is not { } mode)
            return "未修改设置。\n" + Usage;

        await _gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Settings(mode)), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _settingsPath, overwrite: true);
            Volatile.Write(ref _mode, mode);
        }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
        return "执行积极度已设为：" + Label(mode) + "。已保存，从下一轮任务起生效（含新启动的子 Agent）；正在运行的轮次保持原设置。";
    }

    private static string? ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "lazy" or "low" or "懒" or "省力" => "lazy",
        "normal" or "medium" or "标准" or "正常" => "normal",
        "diligent" or "high" or "勤快" or "勤奋" => "diligent",
        "off" or "关闭" => "off",
        _ => null
    };

    private static string Label(string mode) => mode switch
    {
        "lazy" => "省力（lazy）",
        "diligent" => "勤快（diligent）",
        "off" => "关闭干预（off）",
        _ => "标准（normal）"
    };

    private const string Usage = "用法：/ext effort [lazy|normal|diligent|off]；也支持：懒、标准、勤快、关闭。无参数查看当前设置。";
    private const string CommonPolicy = """
        以下是用户选择的任务执行偏好。遵守用户当前任务的明确范围、质量要求、预算、停止条件和宿主权限；冲突时优先满足这些约束。
        积极度不代表降低正确性：禁止编造完成情况或测试结果。省力也必须完成已授权的任务；勤快也不能擅自扩大任务范围、执行未获授权的外部操作或绕过审批。
        按任务需要决定工具、技能和子 Agent 的使用，不为表现勤快而增加无意义调用。遇到确实需要用户信息或授权的阻塞时，明确说明并请求所需输入。
        """;

    private static string Policy(string mode) => mode switch
    {
        "lazy" => """
            执行风格：省力。选择能完整满足需求的最短可靠路径。先做定向检查，避免与目标无关的广泛搜索、额外重构和可选增强。
            直接完成必要工作，执行与风险相称的关键验证；验证通过后及时交付。出现失败先尝试最直接的合理修复，避免反复尝试同一失败方案。
            不因省力而跳过用户要求的步骤、必要验证或留下未完成的占位实现。回复简洁，说明结果和仍存在的限制。
            """,
        "diligent" => """
            执行风格：勤快。主动把已授权任务推进到可交付状态，不在只给出计划、建议或半成品时停下。
            开始前检查直接相关的上下文、现有约定和依赖。实施后主动检查与变更有关的边界情况，运行适当验证，修复发现的问题，再验证受影响部分。
            遇到可自行排查的错误，先查证原因并尝试不同的合理方案；记录已失败的路径，避免无进展循环。确实无法继续时给出证据和具体阻塞。
            只在新增变更、失败或未解决风险确有需要时扩大或重复验证。交付时简述完成内容、验证证据及剩余限制，不通过增加篇幅代替工作。
            """,
        _ => """
            执行风格：标准。兼顾完整性与成本，先理解目标，再完成必要实施和适当验证。主动处理直接相关的问题，避免不必要的额外探索。
            以满足用户明确要求为交付条件，清晰报告结果、已做验证与真实限制。
            """
    };

    private sealed record Settings(string Mode);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
