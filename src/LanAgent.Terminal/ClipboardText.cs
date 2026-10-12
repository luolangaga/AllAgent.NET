using System.Runtime.InteropServices;

namespace LanAgent.Terminal;

internal static class ClipboardText
{
    public static string? ReadImageReference()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pngFormat = RegisterClipboardFormat("PNG");
        if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat) && Open(IntPtr.Zero))
        {
            try
            {
                var handle = GetClipboardData(pngFormat); var size = handle == IntPtr.Zero ? 0 : (long)GlobalSize(handle);
                if (size is > 0 and <= 4_000_000)
                {
                    var pointer = GlobalLock(handle);
                    if (pointer != IntPtr.Zero)
                    {
                        try
                        {
                            var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            var folder = Path.Combine(Path.GetTempPath(), "LanAgent-clipboard"); Directory.CreateDirectory(folder);
                            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(path, bytes);
                            return "\"" + path + "\"";
                        }
                        finally { GlobalUnlock(handle); }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { CloseClipboard(); }
        }
        if (!(IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17))) return null;
        // Fixed STA helper for Windows DIB/PNG clipboard formats. It is called
        // only on a human paste gesture, never through an agent tool.
        const string script = """
            $ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); Add-Type -AssemblyName System.Windows.Forms;
            if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
              $image=[System.Windows.Forms.Clipboard]::GetImage();
              try {
                $folder=Join-Path $env:TEMP 'LanAgent-clipboard';
                [System.IO.Directory]::CreateDirectory($folder) | Out-Null;
                $path=Join-Path $folder ([guid]::NewGuid().ToString('N')+'.png');
                $image.Save($path,[System.Drawing.Imaging.ImageFormat]::Png);
                [Console]::Write($path);
              } finally { $image.Dispose() }
            }
            """;
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-Command", script }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(4000)) { process.Kill(true); return null; }
            var path = stdout.GetAwaiter().GetResult().Trim(); _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 && File.Exists(path) ? "\"" + path + "\"" : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
    // Access only in response to the user's paste/copy gestures, never from model tools.
    private static bool Open(IntPtr owner)
    {
        for (var attempt = 0; attempt < 5; attempt++) { if (OpenClipboard(owner)) return true; Thread.Sleep(10); }
        return false;
    }
    public static string? Read()
    {
        if (!OperatingSystem.IsWindows() || !Open(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(13); if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle); if (pointer == IntPtr.Zero) return null;
            try
            {
                var length = Math.Min((long)GlobalSize(handle) / 2, 32001);
                var chars = new char[length]; Marshal.Copy(pointer, chars, 0, (int)length);
                var end = Array.IndexOf(chars, '\0'); return new string(chars, 0, end < 0 ? (int)Math.Min(length, 32000) : Math.Min(end, 32000));
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    public static bool Write(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var owner = GetConsoleWindow(); var created = owner == IntPtr.Zero;
        if (created) owner = CreateWindowEx(0, "STATIC", "LanAgent clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) return false;
        var memory = IntPtr.Zero;
        try
        {
            if (!Open(owner)) return false;
            try
            {
                var chars = (text.Replace("\r\n", "\n").Replace("\n", "\r\n") + '\0').ToCharArray();
                memory = GlobalAlloc(2, (nuint)(chars.Length * 2)); if (memory == IntPtr.Zero) return false;
                var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) return false;
                try { Marshal.Copy(chars, 0, pointer, chars.Length); } finally { GlobalUnlock(memory); }
                if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) return false;
                memory = IntPtr.Zero; // Ownership transferred to Windows.
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); if (created) DestroyWindow(owner); }
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr handle);
}

/// <summary>While editing, Ctrl+C must be a key so selected text can be copied instead of cancelling.</summary>
internal sealed class TextInputMode : IDisposable
{
    private readonly bool? _original;
    public TextInputMode()
    {
        try { if (!Console.IsInputRedirected) { _original = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } }
        catch (IOException) { }
    }
    public void Dispose() { if (_original is { } original) { try { Console.TreatControlCAsInput = original; } catch (IOException) { } } }
}
