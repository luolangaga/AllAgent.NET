using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AllAgent.NET.Gui.Interop;

/// <summary>
/// Windows 11 window styling: rounded corners, Mica backdrop, immersive dark title
/// bar and a native border colour. Every call is best effort — Windows 10 ignores the
/// attributes, so the shell falls back to a solid chrome.
/// </summary>
internal static class NativeWindow
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmcpRound = 2;
    private const int DwmsbtMainWindow = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Applies the native look and reports whether the Mica backdrop took effect.</summary>
    public static bool Apply(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero) return false;
        var mode = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref mode, sizeof(int));
        var corner = DwmcpRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var backdrop = DwmsbtMainWindow;
        var mica = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        var border = dark ? unchecked((int)0x24FFFFFF) : unchecked((int)0x18000000);
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        return mica;
    }

    /// <summary>A borderless window still maximizes over the taskbar; clamp it to the monitor work area.</summary>
    public static void HookMinMax(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source) source.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        bounds.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        bounds.MaxSize.X = info.Work.Right - info.Work.Left;
        bounds.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    {
        public NativePoint Reserved; public NativePoint MaxSize; public NativePoint MaxPosition;
        public NativePoint MinTrackSize; public NativePoint MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
