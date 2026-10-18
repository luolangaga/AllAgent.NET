using System.Diagnostics;
using System.IO;
using System.Windows;
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
    [STAThread]
    private static int Main(string[] args)
    {
        ShellOptions options;
        try { options = ShellOptions.Parse(args); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AllAgent.NET", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(options);
            application.Run(window);
            return window.ExitCode;
        }
        catch (Exception ex)
        {
            Log("startup failed: " + ex);
            MessageBox.Show("GUI 启动失败：" + ex.Message, "AllAgent.NET", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
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
}
