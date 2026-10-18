using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using AllAgent.NET.Gui.Interop;
using Microsoft.Web.WebView2.Core;

namespace AllAgent.NET.Gui;

/// <summary>
/// The GUI shell window: a native Windows 11 styled frame around the Web UI the
/// terminal serves. It owns no agent runtime — the page talks to the loopback
/// backend over the bridge WebSocket.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellOptions _options;
    private readonly bool _dark;
    private bool _mica;

    /// <summary>Non-zero when the shell itself failed; the host treats a closed window as success.</summary>
    public int ExitCode { get; private set; }

    public MainWindow(ShellOptions options)
    {
        _options = options;
        InitializeComponent();
        Title = options.Title;
        TitleText.Text = options.Title;
        if (options.DevTools) DevToolsButton.Visibility = Visibility.Visible;
        _dark = NativeWindow.IsSystemDark();
        ApplyTheme();
        RefreshButton.Click += (_, _) => Refresh();
        BrowserButton.Click += (_, _) => OpenInBrowser();
        DevToolsButton.Click += (_, _) => View.CoreWebView2?.OpenDevToolsWindow();
        MinimizeButton.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        MaximizeButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) => OnStateChanged();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _mica = NativeWindow.Apply(handle, _dark);
        NativeWindow.HookMinMax(this);
        ApplyTheme();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            Directory.CreateDirectory(_options.CacheDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _options.CacheDirectory);
            await View.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Program.Log("webview2 initialization failed: " + ex);
            MessageBox.Show(this, "无法启动 WebView2：" + ex.Message + "\n\n请安装 Microsoft Edge WebView2 Runtime 后重试。",
                "AllAgent.NET", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitCode = 3;
            Close();
            return;
        }
        var core = View.CoreWebView2;
        core.Settings.AreDevToolsEnabled = _options.DevTools;
        core.Settings.AreDefaultContextMenusEnabled = _options.DevTools;
        core.Settings.IsStatusBarEnabled = false;
        try { View.DefaultBackgroundColor = _dark ? System.Drawing.Color.FromArgb(0x10, 0x10, 0x14) : System.Drawing.Color.White; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += (_, failure) => Program.Log("webview process failed: " + failure.ProcessFailedKind);
        core.Navigate(_options.Url);
        WatchHost();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // The shell is not a browser: links that ask for a new window go to the system browser.
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https") Program.OpenInBrowser(target.ToString());
    }

    /// <summary>Closing the window ends the session; a dying host must take the window with it.</summary>
    private void WatchHost()
    {
        var parent = _options.ParentProcessId;
        if (parent <= 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var host = Process.GetProcessById(parent);
                host.WaitForExit();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            try { Dispatcher.Invoke(Close); }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException) { }
        });
    }

    private void Refresh()
    {
        if (View.CoreWebView2 is { } core) core.Reload();
        else Program.Log("refresh requested before WebView2 was ready");
    }

    private void OpenInBrowser()
    {
        var current = View.CoreWebView2?.Source;
        Program.OpenInBrowser(string.IsNullOrWhiteSpace(current) ? _options.Url : current);
    }

    private void OnStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        Root.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { View.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        Program.Log("window closed");
    }

    private void ApplyTheme()
    {
        // Mica is only visible while the chrome stays transparent; Windows 10 gets a solid frame instead.
        var surface = _mica ? Colors.Transparent : _dark ? Color.FromRgb(0x12, 0x12, 0x16) : Color.FromRgb(0xF5, 0xF5, 0xF8);
        Resources["ChromeSurface"] = new SolidColorBrush(surface);
        Resources["ChromeForeground"] = new SolidColorBrush(_dark ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x1B, 0x1B, 0x1F));
        Resources["ChromeMuted"] = new SolidColorBrush(_dark ? Color.FromRgb(0x9A, 0x9A, 0xA4) : Color.FromRgb(0x62, 0x62, 0x6B));
        Resources["ChromeHover"] = new SolidColorBrush(_dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0, 0, 0));
        Resources["ChromeBorder"] = new SolidColorBrush(_dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0));
    }
}
