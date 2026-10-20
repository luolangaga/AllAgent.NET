using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AllAgent.NET.Diagnostics;
using AllAgent.NET.Gui.Interop;

namespace AllAgent.NET.Gui;

/// <summary>Command line of the GUI shell: it only wraps the Web UI the terminal serves.</summary>
public sealed record ShellOptions(string Url, string Title, string CacheDirectory, bool DevTools, int ParentProcessId)
{
    public static ShellOptions Parse(string[] args)
    {
        string? url = null, title = null, cache = null;
        var devTools = false;
        var parent = 0;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--url" when ++index < args.Length: url = args[index]; break;
                case "--title" when ++index < args.Length: title = args[index]; break;
                case "--cache" when ++index < args.Length: cache = args[index]; break;
                case "--parent" when ++index < args.Length && int.TryParse(args[index], out var pid): parent = pid; break;
                case "--devtools": devTools = true; break;
            }
        }
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("--url 需要 http(s) 地址：" + (url ?? "<缺失>"));
        var directory = string.IsNullOrWhiteSpace(cache)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllAgent.NET", "gui", "webview2")
            : Path.GetFullPath(cache);
        return new ShellOptions(url, string.IsNullOrWhiteSpace(title) ? "AllAgent.NET" : title, directory, devTools, parent);
    }
}

internal static class Program
{
    private const uint IconError = 0x00000010;
    private const uint YesNo = 0x00000004;
    private const int IdYes = 6;

    [STAThread]
    private static int Main(string[] args)
    {
        CrashReporter.Install("gui-shell", onReported: NotifyCrash);
        ShellOptions options;
        try { options = ShellOptions.Parse(args); }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, ex.Message, "AllAgent.NET", IconError);
            return 2;
        }
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.DispatcherUnhandledException += (_, e) =>
            {
                // The report is written synchronously and the prompt offers the GitHub page.
                // The shell cannot recover a broken dispatcher, so it shuts down deliberately.
                e.Handled = true;
                CrashReporter.Report(e.Exception, "gui-shell", fatal: true);
                application.Shutdown(4);
            };
            var window = new MainWindow(options);
            application.Run(window);
            return window.ExitCode;
        }
        catch (Exception ex)
        {
            Log("startup failed: " + ex);
            if (CrashReporter.Report(ex, "gui-shell", fatal: true) == null)
                MessageBox(IntPtr.Zero, "GUI 启动失败：" + ex.Message, "AllAgent.NET", IconError);
            return 1;
        }
    }

    private static void NotifyCrash(CrashReport report)
    {
        if (!report.Fatal)
        {
            Log("non-fatal crash report: " + report.MarkdownPath);
            return;
        }
        var answer = MessageBox(IntPtr.Zero,
            "AllAgent.NET 发生未处理的异常，窗口即将关闭。\n\n诊断报告：\n" + report.MarkdownPath +
            "\n\n是否打开 GitHub 提交页面？", "AllAgent.NET", YesNo | IconError);
        CrashReporter.MarkNotified(report);
        if (answer != IdYes) return;
        if (CrashReporter.OpenIssue(report)) CrashReporter.MarkReported(report);
        else MessageBox(IntPtr.Zero, "无法打开浏览器，请手动打开：\n" + report.UrlPath, "AllAgent.NET", IconError);
    }

    internal static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "allagent.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Gui] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log("open in browser failed: " + ex.Message); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
