using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AllAgent.NET.Core.Runtime;

/// <summary>Shows a path to the user. What a browser renders itself — a generated page, a picture,
/// a clip — opens in the default browser's own new window, so the result is visible at once;
/// everything else goes to Explorer, which hands it to the application that owns the type.</summary>
internal static class PathOpener
{
    /// <summary>Extensions a browser displays unaided; the rest is not its business.</summary>
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        "htm", "html", "xht", "xhtml", "svg",
        "png", "jpg", "jpeg", "jfif", "gif", "webp", "bmp", "ico", "avif",
        "mp4", "m4v", "webm", "ogv", "mov", "mp3", "m4a", "wav", "oga", "ogg", "aac", "flac", "opus",
        "pdf", "txt", "log", "md", "json", "csv", "xml",
    };

    public static bool CanPreview(string path) => Previewable.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>Opens a file or a directory. False when the path is not there or nothing could be started.</summary>
    public static bool Open(string path)
    {
        if (File.Exists(path)) return (CanPreview(path) && TryPreview(path)) || TryExplorer(path);
        return Directory.Exists(path) && TryExplorer(path);
    }

    private static bool TryPreview(string path)
    {
        string url;
        try { url = new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { return false; }
        var browser = DefaultBrowser();
        if (browser != null)
        {
            // Chromium and Firefox both take a window flag; without one the file lands in a tab
            // of whichever window happens to be open.
            var flag = Path.GetFileName(browser).Contains("firefox", StringComparison.OrdinalIgnoreCase) ? "-new-window" : "--new-window";
            try
            {
                using var process = Process.Start(new ProcessStartInfo(browser) { UseShellExecute = true, Arguments = $"{flag} \"{url}\"" });
                return true;
            }
            catch { /* the shell is still worth a try */ }
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private static bool TryExplorer(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Windows' own answer to "what opens an http address?" — the default browser,
    /// or null when the association cannot be read.</summary>
    internal static string? DefaultBrowser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // The shell APIs refuse to name the current default (UserChoice is hash-protected against
            // hijacking), but the choice itself is readable, and its ProgId names the command to run.
            var progId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice", "ProgId", null) as string;
            if (string.IsNullOrWhiteSpace(progId)) return null;
            var command = Registry.GetValue($@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", string.Empty, null) as string;
            return Executable(command);
        }
        catch { return null; }
    }

    /// <summary>The executable in a shell command line, quoted (`"C:\…\msedge.exe" --single-argument %1`) or not.</summary>
    internal static string? Executable(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var executable = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executable >= 0 ? text[..(executable + 4)] : text.Split(' ', 2)[0];
    }
}
