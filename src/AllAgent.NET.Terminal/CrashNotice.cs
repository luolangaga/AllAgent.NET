using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using AllAgent.NET.Core.Localization;
using AllAgent.NET.Diagnostics;
using L = AllAgent.NET.Core.Localization.HostText;

namespace AllAgent.NET.Terminal;

/// <summary>Surfaces crash bundles: once at startup and immediately after a fatal host crash.</summary>
internal static class CrashNotice
{
    private static readonly string[] Disabled = ["1", "true", "yes", "on"];

    /// <summary>
    /// Top-level command handlers turn expected failures into one-line messages. Anything else
    /// is a programming error worth a bundle, but it is non-fatal and does not prompt.
    /// </summary>
    public static void ReportUnexpected(Exception exception, string? component = null)
    {
        if (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or HttpRequestException or TimeoutException
            or Win32Exception or FormatException) return;
        CrashReporter.Report(exception, component ?? (TerminalSession.GuiHost ? "gui-host" : "terminal"), fatal: false, notify: false);
    }

    public static void ShowPending(bool guiHost, bool quiet = false)
    {
        if (quiet) return;
        var setting = Environment.GetEnvironmentVariable("ALLAGENT_NO_CRASH_PROMPT");
        if (setting != null && Disabled.Contains(setting.Trim().ToLowerInvariant())) return;
        IReadOnlyList<CrashReport> reports;
        try { reports = CrashReporter.GetPendingReports(3); }
        catch { return; }
        if (reports.Count == 0) return;
        var latest = reports[0];
        if (guiHost)
        {
            var more = reports.Count > 1 ? L.Pick($"\n另有 {reports.Count - 1} 个崩溃报告。", $"\n{reports.Count - 1} more crash report(s).") : "";
            var message = L.Pick("上次运行 AllAgent.NET 时发生崩溃。\n\n最新报告：\n", "AllAgent.NET crashed during the previous run.\n\nLatest report:\n")
                + latest.MarkdownPath + more + L.Pick("\n\n是否打开 GitHub 提交页面？", "\n\nOpen the GitHub submission page?");
            var open = GuiShell.AskYesNo(message);
            MarkAll(reports);
            if (open) Open(latest, guiHost: true);
            return;
        }
        try
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(L.Pick($"上次运行发生崩溃（{reports.Count} 个未查看报告）。", $"The previous run crashed ({reports.Count} unseen report(s))."));
            Console.Error.WriteLine(L.Pick("诊断报告：", "Diagnostic report: ") + latest.MarkdownPath);
            Console.Error.WriteLine(L.Pick("查看：allagent crash list    提交：allagent crash open", "List: allagent crash list    Submit: allagent crash open"));
            Console.Error.Flush();
        }
        catch { /* no console */ }
        MarkAll(reports);
    }

    public static void Reported(CrashReport report, bool guiHost)
    {
        if (!report.Fatal) return;
        if (guiHost)
        {
            var message = L.Pick("AllAgent.NET 发生未处理的异常，窗口即将关闭。\n\n诊断报告：\n", "AllAgent.NET hit an unhandled exception and the window will close.\n\nDiagnostic report:\n")
                + report.MarkdownPath + L.Pick("\n\n是否打开 GitHub 提交页面？", "\n\nOpen the GitHub submission page?");
            var open = GuiShell.AskYesNo(message);
            CrashReporter.MarkNotified(report);
            if (open) Open(report, guiHost: true);
            return;
        }
        try
        {
            if (!Console.IsErrorRedirected) Console.Error.Write("\u001b[0m\u001b[?1049l\u001b[?25h");
            Console.Error.WriteLine();
            Console.Error.WriteLine(L.Pick("AllAgent.NET 遇到未处理的异常，即将退出。", "AllAgent.NET hit an unhandled exception and is exiting."));
            Console.Error.WriteLine(L.Pick("异常：", "Exception: ") + report.ExceptionType + ": " + report.Message);
            Console.Error.WriteLine(L.Pick("诊断报告：", "Diagnostic report: ") + report.MarkdownPath);
            if (!string.IsNullOrEmpty(report.DumpPath)) Console.Error.WriteLine(L.Pick("内存转储：", "Memory dump: ") + report.DumpPath);
            Console.Error.WriteLine(L.Pick("提交到 GitHub：allagent crash open（附上 report.md）", "Submit on GitHub: allagent crash open (attach report.md)"));
            Console.Error.Flush();
        }
        catch { /* no console */ }
        // Leave the bundle unseen: the next normal launch shows the path and the submit command.
    }

    private static void MarkAll(IReadOnlyList<CrashReport> reports)
    {
        foreach (var report in reports) CrashReporter.MarkNotified(report);
    }

    private static void Open(CrashReport report, bool guiHost)
    {
        if (CrashReporter.OpenIssue(report)) { CrashReporter.MarkReported(report); return; }
        var message = L.Pick("无法打开浏览器，请手动访问：\n", "Could not open the browser. Open this file manually:\n") + report.UrlPath;
        if (guiHost) GuiShell.ShowFatal(message);
        else
        {
            try { Console.Error.WriteLine(message); } catch { }
        }
    }
}
