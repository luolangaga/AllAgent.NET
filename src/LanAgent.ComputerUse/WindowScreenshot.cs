using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LanAgent.Core.Tools;

namespace LanAgent.ComputerUse;

internal static class WindowScreenshot
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    internal static ToolResult Capture(nint handle, string title, string mode)
    {
        if (!GetWindowRect(handle, out var rect)) return ToolResult.Failure("Window bounds are unavailable.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 10000 || height > 10000 || (long)width * height > 32000000) return ToolResult.Failure("Window is minimized or its capture size is unsupported.");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc(); bool ok;
            try { ok = PrintWindow(handle, dc, 2); } finally { graphics.ReleaseHdc(dc); }
            if (!ok) return ToolResult.Failure("PrintWindow could not capture this window. No focus change or desktop screenshot fallback was performed.");
        }
        var scale = Math.Min(1.0, 1600.0 / Math.Max(width, height));
        using var resized = new Bitmap(bitmap, new Size(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale))));
        using var stream = new MemoryStream(); resized.Save(stream, ImageFormat.Png);
        if (stream.Length > 4 * 1024 * 1024) return ToolResult.Failure("Captured PNG exceeds the image budget.");
        return ToolResult.Success(new { windowTitle = title, mode, width = resized.Width, height = resized.Height, originalWidth = width, originalHeight = height,
            captureMethod = "PrintWindow", note = "GPU/protected windows may return a blank or incomplete frame; inspect the image, not just the API result.", imageBase64 = Convert.ToBase64String(stream.ToArray()) });
    }
}
