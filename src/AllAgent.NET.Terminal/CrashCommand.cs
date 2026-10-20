using AllAgent.NET.Core.Localization;
using AllAgent.NET.Diagnostics;
using Spectre.Console;
using L = AllAgent.NET.Core.Localization.HostText;

namespace AllAgent.NET.Terminal;

/// <summary>`allagent crash`: inspect local crash bundles and open the GitHub submission page.</summary>
internal static class CrashCommand
{
    public static int Execute(TerminalArguments args, TerminalUi ui)
    {
        var action = args.Positionals.FirstOrDefault() ?? "list";
        var id = args.Positionals.ElementAtOrDefault(1);
        var reports = CrashReporter.ListReports();
        switch (action)
        {
            case "path":
                ManagementUi.Output(ui, new { root = CrashReporter.RootDirectory },
                    () => TerminalOutput.Write(new Text(CrashReporter.RootDirectory + Environment.NewLine)));
                return 0;
            case "list":
            {
                var recent = reports.Count(report => DateTimeOffset.UtcNow - report.TimestampUtc <= TimeSpan.FromDays(7));
                ManagementUi.Output(ui, new
                {
                    root = CrashReporter.RootDirectory,
                    total = reports.Count,
                    last7Days = recent,
                    reports = reports.Select(report => new
                    {
                        report.Id,
                        timestampUtc = report.TimestampUtc,
                        report.Component,
                        report.Fatal,
                        report.ExceptionType,
                        report.Message,
                        path = report.MarkdownPath,
                        report.IssueUrl,
                        report.Notified,
                        report.Reported,
                        dump = report.DumpPath,
                    }),
                }, () =>
                {
                    if (reports.Count == 0)
                    {
                        TerminalOutput.Write(new Text(L.Pick("没有崩溃报告。\n", "No crash reports.\n")));
                        TerminalOutput.Write(new Text(L.Pick("目录：", "Directory: ") + CrashReporter.RootDirectory + "\n"));
                        return;
                    }
                    var table = new Table().Title(L.Pick($"崩溃报告（共 {reports.Count} 个，最近 7 天 {recent} 个）", $"Crash reports ({reports.Count} total, {recent} in the last 7 days)"))
                        .AddColumn(L.Pick("时间", "Time"))
                        .AddColumn(L.Pick("组件", "Component"))
                        .AddColumn(L.Pick("异常", "Exception"))
                        .AddColumn(L.Pick("状态", "Status"))
                        .AddColumn(L.Pick("报告", "Report"));
                    foreach (var report in reports.Take(20))
                    {
                        var state = report.Reported ? L.Pick("已提交", "submitted")
                            : report.Notified ? L.Pick("已提示", "seen")
                            : L.Pick("未处理", "new");
                        table.AddRow(
                            Markup.Escape(report.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                            Markup.Escape(report.Component),
                            Markup.Escape(Clip(report.ExceptionType + ": " + report.Message, 70)),
                            Markup.Escape(state),
                            Markup.Escape(Path.GetFileName(report.Folder)));
                    }
                    TerminalOutput.Write(table);
                    TerminalOutput.Write(new Text(L.Pick(
                        $"目录：{CrashReporter.RootDirectory}\n查看：allagent crash show [ID]    提交：allagent crash open [ID]\n",
                        $"Directory: {CrashReporter.RootDirectory}\nShow: allagent crash show [ID]    Submit: allagent crash open [ID]\n")));
                });
                return 0;
            }
            case "show":
            {
                var report = Find(reports, id);
                var content = ReadReport(report);
                ManagementUi.Output(ui, new { id = report.Id, path = report.MarkdownPath, content },
                    () => TerminalOutput.Write(new Text(content + Environment.NewLine)));
                return 0;
            }
            case "open":
            {
                var report = Find(reports, id);
                if (!CrashReporter.OpenIssue(report))
                    throw new InvalidOperationException(L.Pick("无法打开浏览器，请手动打开：", "Could not open the browser; open this file manually: ") + report.UrlPath);
                CrashReporter.MarkReported(report);
                ManagementUi.Success(ui, L.Pick("已打开 GitHub 提交页面；请附上 report.md。", "Opened the GitHub submission page; attach report.md."));
                return 0;
            }
            default:
                throw new ArgumentException(L.Pick("crash 支持 list、show [ID]、open [ID]、path。", "crash supports list, show [ID], open [ID], path."));
        }
    }

    private static CrashReport Find(IReadOnlyList<CrashReport> reports, string? id)
    {
        if (reports.Count == 0) throw new InvalidOperationException(L.Pick("没有崩溃报告。", "No crash reports."));
        if (string.IsNullOrWhiteSpace(id) || id is "latest" or "last") return reports[0];
        var match = reports.FirstOrDefault(report => report.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(report.Folder).Contains(id, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException(L.Pick($"没有找到崩溃报告：{id}", $"No crash report matches: {id}"));
    }

    private static string ReadReport(CrashReport report)
    {
        try { return File.ReadAllText(report.MarkdownPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return L.Pick("无法读取报告：", "Could not read the report: ") + ex.Message + Environment.NewLine + report.MarkdownPath;
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
