using Avalonia.Controls;
using LanAgent.Core.Bridge;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LanAgent.Desktop;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly string _startUrl;
    private NativeWebView? _webView;

    public MainWindow() : this(App.Services, "about:blank") { }

    public MainWindow(IServiceProvider services, string startUrl)
    {
        _services = services;
        _startUrl = startUrl;
        InitializeComponent();

        _webView = new NativeWebView
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        // Wire up the bridge event pusher so streaming events can reach the WebView
        var eventPusher = _services.GetRequiredService<BridgeEventPusher>();
        eventPusher.SetPusher(PushEventToWebView);

        var panel = this.FindControl<Panel>("RootPanel")!;
        panel.Children.Add(_webView);

        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.WebMessageReceived += OnWebMessageReceived;

        _webView.Navigate(new Uri(_startUrl));
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        LogToFile($"[OnNavigationCompleted] IsSuccess={e.IsSuccess}");
    }

    private void PushEventToWebView(string json)
    {
        if (_webView == null) return;
        var escaped = EscapeForJs(json);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _webView.InvokeScript($"window.__bridgeCallback && window.__bridgeCallback('{escaped}')"));
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_webView == null || string.IsNullOrEmpty(e.Body)) return;
        var dispatcher = _services.GetRequiredService<BridgeDispatcher>();
        var json = e.Body;

        Task.Run(async () =>
        {
            try
            {
                var responseJson = await dispatcher.InvokeAsync(json);
                var escaped = EscapeForJs(responseJson);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    _webView.InvokeScript($"window.__bridgeCallback && window.__bridgeCallback('{escaped}')"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bridge] Handler error: {ex}");
                LogToFile($"[WebMessageResponse] ERROR: {ex}");
            }
        });
    }

    private static void LogToFile(string message)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logPath = Path.Combine(appData, "LanAgent", "lanagent.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MainWindow] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { /* logging must never crash */ }
    }

    private static string EscapeForJs(string json) =>
        json.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n");
}
