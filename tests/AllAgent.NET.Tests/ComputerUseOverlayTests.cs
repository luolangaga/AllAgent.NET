using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class ComputerUseOverlayTests
{
    [Fact]
    public async Task FullscreenIndicatorPreservesFocusAndHitTestingAndExitsOnPipeClosure()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        var point = new Point { X = 100, Y = 100 };
        var info = BoundedProcess.StartInfo(new ComputerUseState().WorkerPath, ["--overlay", "ForegroundAllowed"]);
        info.RedirectStandardInput = true;
        using var overlay = new Process { StartInfo = info };
        overlay.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Assert.Equal("ready", await overlay.StandardOutput.ReadLineAsync(deadline.Token));
            var displayed = Stopwatch.StartNew();
            var windows = WindowsForProcess(overlay.Id); Assert.NotEmpty(windows);
            // Other apps or the user may change focus while the worker starts.
            // Check the overlay's ownership rather than freezing global desktop state.
            Assert.DoesNotContain(GetForegroundWindow(), windows);
            Assert.DoesNotContain(WindowFromPoint(point), windows);
            var monitors = new List<Rect>();
            EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint data) => { monitors.Add(rect); return true; }, 0);
            Assert.Equal(monitors.Count, windows.Count);
            foreach (var window in windows)
            {
                Assert.True(GetWindowRect(window, out var rect));
                Assert.Contains(monitors, m => m.Left == rect.Left && m.Top == rect.Top && m.Right == rect.Right && m.Bottom == rect.Bottom);
                var style = GetWindowLong(window, -20);
                Assert.Equal(0x080800A8, style & 0x080800A8); // NoActivate, Layered, Transparent, ToolWindow, Topmost.
            }
            overlay.StandardInput.Close();
            await overlay.WaitForExitAsync(deadline.Token);
            Assert.True(displayed.Elapsed >= TimeSpan.FromSeconds(3), "Even pipe closure must retain the indicator for at least three seconds.");
            Assert.Equal(0, overlay.ExitCode);
            Assert.Empty(WindowsForProcess(overlay.Id));
        }
        finally { BoundedProcess.Kill(overlay); }
    }

    [Fact]
    public async Task IdleExitWaitsThreeSecondsAndNewInputReusesWindowsWithoutFlashing()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        var info = BoundedProcess.StartInfo(new ComputerUseState().WorkerPath, ["--overlay", "BackgroundOnly"]);
        info.RedirectStandardInput = true;
        using var overlay = new Process { StartInfo = info }; overlay.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Assert.Equal("ready", await overlay.StandardOutput.ReadLineAsync(deadline.Token));
            var originalWindows = WindowsForProcess(overlay.Id);
            overlay.StandardInput.WriteLine("idle"); overlay.StandardInput.Flush();
            await Task.Delay(1500);
            Assert.False(overlay.HasExited);
            overlay.StandardInput.WriteLine("active\tForegroundAllowed"); overlay.StandardInput.Flush();
            await Task.Delay(1800); // Cross the old idle deadline; active must have cancelled it.
            Assert.False(overlay.HasExited); Assert.Equal(originalWindows, WindowsForProcess(overlay.Id));
            overlay.StandardInput.WriteLine("idle"); overlay.StandardInput.Flush();
            var idle = Stopwatch.StartNew();
            await Task.Delay(2500);
            Assert.False(overlay.HasExited); Assert.Equal(originalWindows, WindowsForProcess(overlay.Id));
            await overlay.WaitForExitAsync(deadline.Token);
            Assert.True(idle.Elapsed >= TimeSpan.FromSeconds(3)); Assert.Equal(0, overlay.ExitCode);
        }
        finally { overlay.StandardInput.Close(); BoundedProcess.Kill(overlay); }
    }

    [Fact]
    public async Task TaskScopeRetainsIndicatorAcrossObservationsAndOffRevokesIt()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var path = Path.GetFullPath($"../../../../../tests/AllAgent.NET.ComputerUseFixture/bin/{configuration}/net10.0-windows/AllAgent.NET.ComputerUseFixture.exe", AppContext.BaseDirectory);
        using var fixture = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
        Assert.NotNull(fixture);
        var state = new ComputerUseState(); state.Configure(ComputerInteractionMode.BackgroundOnly);
        var tools = new ComputerUseTools(state);
        try
        {
            Assert.True(fixture.WaitForInputIdle(10000));
            var windows = await tools.WindowsAsync(); Assert.True(windows.Ok, windows.Error);
            var id = Assert.Single(JsonSerializer.SerializeToElement(windows.Data).EnumerateArray(), w => w.GetProperty("processId").GetInt32() == fixture.Id).GetProperty("windowId").GetString()!;
            var found = await tools.FindAsync(id, name: "Fixture input", role: "Edit"); Assert.True(found.Ok, found.Error);
            var edit = Assert.Single(JsonSerializer.SerializeToElement(found.Data).GetProperty("elements").EnumerateArray()).GetProperty("elementId").GetString()!;
            var point = new Point { X = 100, Y = 100 };
            using (state.BeginActivityScope())
            {
                var result = await tools.ActionAsync(id, edit, "type_text", "提示层不会拦截后台操作"); Assert.True(result.Ok, result.Error);
                var indicator = FindWindow(null, "AllAgent Computer Use Indicator"); Assert.NotEqual(0, indicator);
                // Some application UIA providers activate their own edit control during SetValue.
                Assert.NotEqual(indicator, GetForegroundWindow()); Assert.NotEqual(indicator, WindowFromPoint(point));
                Assert.True((await tools.WaitAsync(id, name: "Fixture input", role: "Edit", value: "提示层不会拦截后台操作")).Ok);
                windows = await tools.WindowsAsync(); Assert.True(windows.Ok, windows.Error);
                Assert.DoesNotContain("AllAgent Computer Use Indicator", windows.ToJson());
                Assert.NotEqual(0, FindWindow(null, "AllAgent Computer Use Indicator"));
                Assert.True((await tools.ModeAsync("foreground", "Check that mode switching retains the indicator")).Ok);
                Assert.True((await tools.ActionAsync(id, edit, "set_value", "保持同一个提示窗口")).Ok);
                Assert.Equal(indicator, FindWindow(null, "AllAgent Computer Use Indicator"));
                // Switching modes revokes the running operation and hides the indicator.
                state.Configure(ComputerInteractionMode.BackgroundOnly);
                await WaitForNoIndicatorAsync();
            }
            state.Configure(ComputerInteractionMode.BackgroundOnly);
            using (state.BeginActivityScope())
            {
                var failed = await tools.ActionAsync(id, "stale", "invoke"); Assert.False(failed.Ok);
                Assert.NotEqual(0, FindWindow(null, "AllAgent Computer Use Indicator"));
            }
            await WaitForNoIndicatorAsync();
            using var cancellation = new CancellationTokenSource();
            using (state.BeginActivityScope(cancellation.Token))
            {
                Assert.False((await tools.ActionAsync(id, "stale", "invoke")).Ok);
                cancellation.Cancel();
                await WaitForNoIndicatorAsync();
            }
            var directFailure = await tools.ActionAsync(id, "stale", "invoke"); Assert.False(directFailure.Ok);
            await WaitForNoIndicatorAsync();
        }
        finally { state.Configure(ComputerInteractionMode.BackgroundOnly); BoundedProcess.Kill(fixture); }
    }

    private static async Task WaitForNoIndicatorAsync()
    {
        for (var i = 0; i < 100 && FindWindow(null, "AllAgent Computer Use Indicator") != 0; i++) await Task.Delay(50);
        Assert.Equal(0, FindWindow(null, "AllAgent Computer Use Indicator"));
    }
    private static List<nint> WindowsForProcess(int pid)
    {
        var windows = new List<nint>();
        EnumWindows((hwnd, _) => { GetWindowThreadProcessId(hwnd, out var owner); if (owner == pid && IsWindowVisible(hwnd)) windows.Add(hwnd); return true; }, 0);
        return windows;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(nint hwnd, nint data);
    private delegate bool EnumMonitor(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitor callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
}
