using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal static class UsageStatistics
{
    internal const string Note = "自启用统计起累计，仅计入接口返回的用量；含多轮调用、后台任务和上下文压缩。旧历史无法补算；未返回用量的请求不计入 Token 合计。";

    public static void Show(DataStore store, TerminalUi ui)
    {
        var models = store.GetTokenUsageStats();
        var totals = new
        {
            inputTokens = models.Sum(s => s.InputTokens), outputTokens = models.Sum(s => s.OutputTokens),
            totalTokens = models.Sum(s => s.TotalTokens), requests = models.Sum(s => s.Requests),
            requestsWithUsage = models.Sum(s => s.RequestsWithUsage), requestsWithoutUsage = models.Sum(s => s.RequestsWithoutUsage)
        };
        ManagementUi.Output(ui, new { models, totals, note = Note }, () =>
        {
            if (models.Count == 0) TerminalOutput.MarkupLine("[dim]暂无 Token 用量记录，发送消息后可使用 /stats 查看。[/]");
            else TerminalOutput.Write(Render(models));
            TerminalOutput.Write(new Text(Note + "\n", new Style(Color.Grey)));
        });
    }

    internal static Table Render(IReadOnlyList<ModelUsageStats> models)
    {
        var table = new Table().Title("模型 Token 累计用量").AddColumn("提供商").AddColumn("模型")
            .AddColumn(new TableColumn("输入 Token").RightAligned()).AddColumn(new TableColumn("输出 Token").RightAligned())
            .AddColumn(new TableColumn("总 Token").RightAligned()).AddColumn(new TableColumn("请求数").RightAligned())
            .AddColumn(new TableColumn("未返回用量").RightAligned());
        foreach (var s in models) table.AddRow(Markup.Escape(s.ProviderId), Markup.Escape(s.ModelId),
            s.InputTokens.ToString("N0"), s.OutputTokens.ToString("N0"), s.TotalTokens.ToString("N0"),
            s.Requests.ToString("N0"), s.RequestsWithoutUsage.ToString("N0"));
        table.AddRow("合计", "", models.Sum(s => s.InputTokens).ToString("N0"), models.Sum(s => s.OutputTokens).ToString("N0"),
            models.Sum(s => s.TotalTokens).ToString("N0"), models.Sum(s => s.Requests).ToString("N0"), models.Sum(s => s.RequestsWithoutUsage).ToString("N0"));
        return table;
    }
}
