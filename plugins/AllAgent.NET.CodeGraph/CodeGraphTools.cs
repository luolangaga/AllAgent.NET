using System.ComponentModel;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.CodeGraph;

/// <summary>Read-only CodeGraph tools. The plugin injects the code-graph skill, so both tools
/// stay hidden until the model loads it.</summary>
public partial class CodeGraphTools(CodeGraphService service) : IAgentToolPreflight
{
    [AgentTool("codegraph_explore"), Description("Answer architecture, call-flow, where-is-X or impact questions from the pre-built CodeGraph index in one read-only call. Returns verbatim source, call paths and blast radius. Load the code-graph skill first.")]
    public async Task<ToolResult> ExploreAsync(
        [Description("Natural-language question or a set of symbol/file names; not a shell command")] string query,
        CancellationToken ct = default)
    {
        var workspace = Workspace();
        var result = await service.ExploreAsync(workspace, query, ct);
        if (!string.IsNullOrWhiteSpace(result.Error)) return ToolResult.Failure(result.Error);
        var output = ToolResult.Clip(result.Output, ToolResult.MaxOutputCharacters);
        var truncated = result.Truncated || output.Length < result.Output.Length;
        var card = new AgentToolCard(
            Badge: "CODEGRAPH",
            Title: query,
            Summary: truncated ? "输出已截断" : $"{output.Length} 字符",
            Body: "code",
            Text: ToolResult.Clip(output, 4_000),
            Fields:
            [
                new("工作区", Path.GetFileName(workspace)),
                new("同步", result.SyncedAt is { } synced ? synced.ToLocalTime().ToString("HH:mm:ss") : "auto"),
                new("截断", truncated ? "是" : "否", truncated ? "error" : "muted"),
            ]);
        return ToolResult.Success(new { workspace, output, truncated, syncedAt = result.SyncedAt }, truncated) with { Card = card };
    }

    [AgentTool("codegraph_status"), Description("Report whether the current workspace has a CodeGraph index and show its statistics. Read-only. Load the code-graph skill first.")]
    public async Task<ToolResult> StatusAsync(CancellationToken ct = default)
    {
        var status = await service.StatusAsync(Workspace(), ct);
        var card = new AgentToolCard(
            Badge: "CODEGRAPH",
            Title: "索引状态",
            Summary: !status.Installed ? "未安装" : status.Indexed ? "已构建" : "未构建",
            Body: "text",
            Text: ToolResult.Clip(status.Detail, 4_000),
            Fields:
            [
                new("CLI", status.Version ?? "未安装", status.Installed ? "ok" : "error"),
                new("索引", status.Indexed ? "已构建" : "未构建", status.Indexed ? "ok" : "muted"),
                new("工作区", Path.GetFileName(status.Workspace)),
            ]);
        return ToolResult.Success(status) with { Card = card };
    }

    public ToolResult? ValidateInvocation(string name, JsonElement arguments)
    {
        if (name != "codegraph_explore") return null;
        var query = ToolArguments.Get<string>(arguments, "query");
        return CodeGraphService.ValidateQuery(query) is { } error ? ToolResult.Failure(error) : null;
    }

    private static string Workspace() =>
        AgentExecutionScope.WorkspaceRoot is { Length: > 0 } workspace
            ? workspace
            : throw new InvalidOperationException("当前没有活动工作区，无法查询 CodeGraph。");
}
