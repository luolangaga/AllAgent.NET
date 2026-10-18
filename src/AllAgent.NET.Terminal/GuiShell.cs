using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AllAgent.NET.Terminal;

/// <summary>
/// Launches and supervises the WPF GUI shell (gui/AllAgent.NET.Gui.exe) that shows the Web UI
/// in a native window. The terminal keeps ownership of the backend and the window session:
/// closing the window ends the host, and ending the host takes the window down with it.
/// </summary>
internal static class GuiShell
{
    private static readonly List<Mutex> _instances = [];

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "gui", "AllAgent.NET.Gui.exe");
    public static bool Available => File.Exists(Path);

    /// <summary>WebView2 profile shared by every GUI window.</summary>
    public static string CacheDirectory(string sharedDirectory) => System.IO.Path.Combine(sharedDirectory, "gui", "webview2");

    public static string Title(string workspace)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(workspace));
        return string.IsNullOrEmpty(name) ? "AllAgent.NET" : "AllAgent.NET · " + name;
    }

    /// <summary>The built page — or an explicit development server — always pinned to this bridge.</summary>
    public static string Url(string? page, int port)
    {
        var address = string.IsNullOrWhiteSpace(page) ? $"http://127.0.0.1:{port}/" : page.Trim();
        var separator = address.Contains('?') ? '&' : '?';
        return address + separator + "bridge=" + Uri.EscapeDataString($"ws://127.0.0.1:{port}/bridge");
    }

    public static async Task<int> RunAsync(string url, string title, string cacheDirectory, bool devTools = false, CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException("GUI 组件缺失：" + Path + "。请重新构建或重新安装后再试。");
        var info = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        info.ArgumentList.Add("--url"); info.ArgumentList.Add(url);
        info.ArgumentList.Add("--title"); info.ArgumentList.Add(title);
        info.ArgumentList.Add("--cache"); info.ArgumentList.Add(cacheDirectory);
        info.ArgumentList.Add("--parent"); info.ArgumentList.Add(Environment.ProcessId.ToString());
        if (devTools) info.ArgumentList.Add("--devtools");
        using var shell = Process.Start(info) ?? throw new InvalidOperationException("无法启动 GUI 窗口。");
        Log($"window opened at {url} (pid {shell.Id})");
        // Ctrl+C ends the session: the window must not outlive the host that feeds it.
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; Kill(shell); };
        Console.CancelKeyPress += stop;
        try { await shell.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { Kill(shell); throw; }
        finally { Console.CancelKeyPress -= stop; }
        Log($"window exited with code {shell.ExitCode}");
        return shell.ExitCode;
    }

    /// <summary>One GUI window per data directory: a second host would fight over the store lease.</summary>
    public static bool TryAcquireInstance(string dataDirectory)
    {
        var name = "allagent-gui-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataDirectory)).ToUpperInvariant())))[..32];
        try
        {
            var mutex = new Mutex(true, name, out var created);
            if (!created) { mutex.Dispose(); return false; }
            _instances.Add(mutex);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return true; }
    }

    /// <summary>The GUI host has no console; a broken inherited handle must never fault the session.</summary>
    public static void SilenceOutput()
    {
        try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public static void ShowFatal(string message)
    {
        Log(message);
        try { MessageBox(IntPtr.Zero, message, "AllAgent.NET", 0x00000010); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public static void Log(string message)
    {
        try
        {
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET");
            Directory.CreateDirectory(directory);
            File.AppendAllText(System.IO.Path.Combine(directory, "allagent.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [GuiHost] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
