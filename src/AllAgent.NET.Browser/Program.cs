using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using AllAgent.NET.Core.Browser;
using AllAgent.NET.Core.Tools;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AllAgent.NET.Browser;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        if (args is ["--check"])
        {
            try { Console.WriteLine(ToolResult.Success(new { webView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString() }).ToJson()); }
            catch (Exception ex) { Console.WriteLine(ToolResult.Failure("WebView2 Runtime missing: " + ex.Message).ToJson()); Environment.ExitCode = 1; }
            return;
        }
        var profile = args.Length == 2 && args[0] == "--profile" ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllAgent.NET", "browser-use");
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        application.Run(new BrowserWindow(profile));
    }
}

/// <summary>
/// WPF browser worker: one WebView2 per tab, driven over the LANBROWSER line protocol on
/// stdin/stdout. The window is the automation surface, so a visible top-level window and a
/// responsive message pump are part of the contract.
/// </summary>
internal sealed class BrowserWindow : Window, IBrowserConnection
{
    private static readonly Brush ChromeBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x20));
    private static readonly Brush SurfaceBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2D));

    private readonly string _profile;
    private readonly TabControl _tabs = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBox _address = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
        CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 12.5
    };
    private readonly Dictionary<string, WebView2> _views = [];
    private readonly Dictionary<TabItem, string> _ids = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (CoreWebView2ScriptDialogOpeningEventArgs Args, CoreWebView2Deferral Deferral)> _dialogs = [];
    private CoreWebView2Environment? _environment;
    private BrowserAutomationSession? _session;
    private bool _disposed;
    private TaskCompletionSource _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserWindow(string profile)
    {
        _profile = profile;
        Title = "AllAgent Browser · WebView2";
        Width = 1180; Height = 820; MinWidth = 640; MinHeight = 420;
        Background = ChromeBrush;
        Content = BuildLayout();
        Loaded += OnLoadedAsync;
        Closed += OnClosed;
    }

    public JsonElement? Dialog => _dialogs.TryGetValue(ActiveTabId, out var pending)
        ? JsonSerializer.SerializeToElement(new { type = pending.Args.Kind.ToString(), message = pending.Args.Message, defaultPrompt = pending.Args.DefaultText }) : null;
    public string ActiveTabId => _tabs.SelectedItem is TabItem selected && _ids.TryGetValue(selected, out var id) ? id : "";
    private WebView2 Current => _views.TryGetValue(ActiveTabId, out var view) ? view : throw new InvalidOperationException("No active tab. Open a new tab.");

    private UIElement BuildLayout()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0), Height = 38
        };
        toolbar.Children.Add(ToolbarButton("\uE72B", "后退", () => { if (Current.CanGoBack) Current.GoBack(); }));
        toolbar.Children.Add(ToolbarButton("\uE72A", "前进", () => { if (Current.CanGoForward) Current.GoForward(); }));
        toolbar.Children.Add(ToolbarButton("\uE72C", "刷新", () => Current.Reload()));
        var address = new Border
        {
            Background = SurfaceBrush, CornerRadius = new CornerRadius(6), Margin = new Thickness(6, 0, 6, 0),
            Height = 28, MinWidth = 320, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 10, 0), Child = _address
        };
        toolbar.Children.Add(address);
        toolbar.Children.Add(ToolbarButton("\uE8A7", "打开地址", NavigateAddress));
        toolbar.Children.Add(ToolbarButton("\uE710", "新建标签", async () => { try { await NewTabAsync(_lifetime.Token); } catch (Exception ex) { _address.Text = ex.Message; } }));
        toolbar.Children.Add(ToolbarButton("\uE8BB", "关闭标签", async () => await CloseTabAsync(ActiveTabId, _lifetime.Token)));
        _address.Style = (Style)XamlReader.Parse(AddressStyle);
        _address.KeyDown += (_, key) => { if (key.Key == Key.Enter) { key.Handled = true; NavigateAddress(); } };
        _tabs.SelectionChanged += (_, _) => { if (_views.TryGetValue(ActiveTabId, out var view)) _address.Text = view.Source?.ToString() ?? "about:blank"; };
        _tabs.ItemContainerStyle = (Style)XamlReader.Parse(TabStyle);

        var dock = new DockPanel { LastChildFill = true };
        var bar = new Border { Background = ChromeBrush, BorderBrush = SurfaceBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = toolbar };
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_tabs);
        return dock;
    }

    private static Button ToolbarButton(string glyph, string tooltip, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Style = (Style)XamlReader.Parse(ButtonStyle) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        };
        return button;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAsync;
        try
        {
            Directory.CreateDirectory(_profile);
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_profile, "webview2"));
            await NewTabAsync(_lifetime.Token);
            _session = new BrowserAutomationSession(this);
            while (!_lifetime.IsCancellationRequested)
            {
                // Standard input may be a synchronous Windows pipe despite the async API.
                // Never enter its read on the message-pump thread.
                var line = await Task.Run(async () => await Console.In.ReadLineAsync(_lifetime.Token));
                if (line == null) break;
                var close = false;
                ToolResult result;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    var op = request.RootElement.GetProperty("operation").GetString()!;
                    close = op == "close";
                    var arguments = request.RootElement.TryGetProperty("arguments", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
                    result = close ? ToolResult.Success(new { closed = true }) : await _session.ExecuteAsync(op, arguments, _lifetime.Token);
                }
                catch (Exception ex) { result = ToolResult.Failure(ex.Message); }
                await WriteResponseAsync(result);
                if (close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await WriteResponseAsync(ToolResult.Failure("WebView2 initialization failed. Install Microsoft Edge WebView2 Runtime: " + ex.Message));
        }
        finally { Close(); }
    }
    private static Task WriteResponseAsync(ToolResult result) => Task.Run(() =>
    {
        // A large screenshot or a slow host reader must not freeze window input.
        Console.WriteLine("LANBROWSER:" + result.ToJson()); Console.Out.Flush();
    });
    private void NavigateAddress()
    {
        try { BrowserAutomationSession.ValidateUrl(_address.Text); Current.CoreWebView2.Navigate(_address.Text); }
        catch (Exception ex) { _address.Text = ex.Message; }
    }
    public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (method == "Page.handleJavaScriptDialog")
        {
            if (!_dialogs.Remove(ActiveTabId, out var dialog)) throw new InvalidOperationException("No pending JavaScript dialog.");
            var args = JsonSerializer.SerializeToElement(parameters);
            if (args.GetProperty("accept").GetBoolean()) { dialog.Args.ResultText = args.GetProperty("promptText").GetString() ?? ""; dialog.Args.Accept(); }
            dialog.Deferral.Complete(); _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return JsonSerializer.SerializeToElement(new { });
        }
        var pending = Current.CoreWebView2.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));
        if (method is "Runtime.evaluate" or "Runtime.releaseObjectGroup" && await Task.WhenAny(pending, _dialogOpened.Task).WaitAsync(ct) != pending)
        {
            _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return method == "Runtime.releaseObjectGroup" ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { result = new { value = new { interruptedByDialog = true, dialog = Dialog } } });
        }
        var json = await pending.WaitAsync(ct);
        using var document = JsonDocument.Parse(json); return document.RootElement.Clone();
    }
    public Task<IReadOnlyList<BrowserTab>> TabsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BrowserTab>>(
        _views.Select(v => new BrowserTab(v.Key, v.Value.Source?.ToString() ?? "about:blank", v.Value.CoreWebView2.DocumentTitle)).ToArray());
    public async Task<string> NewTabAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var page = new TabItem { Header = "新标签" };
        var view = new WebView2();
        page.Content = view;
        _ids[page] = id;
        _tabs.Items.Add(page);
        _tabs.SelectedItem = page;
        try
        {
            await view.EnsureCoreWebView2Async(_environment).WaitAsync(ct);
            _views.Add(id, view);
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            view.CoreWebView2.ScriptDialogOpening += (_, dialog) => { _dialogs[id] = (dialog, dialog.GetDeferral()); _dialogOpened.TrySetResult(); };
            view.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var title = view.CoreWebView2.DocumentTitle;
                page.Header = title[..Math.Min(35, title.Length)];
            };
            view.CoreWebView2.SourceChanged += (_, _) => { if (id == ActiveTabId) _address.Text = view.CoreWebView2.Source; };
            view.CoreWebView2.NewWindowRequested += async (_, request) =>
            {
                var deferral = request.GetDeferral(); request.Handled = true;
                try { var child = await NewTabAsync(_lifetime.Token); request.NewWindow = _views[child].CoreWebView2; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
                finally { deferral.Complete(); }
            };
            view.CoreWebView2.WindowCloseRequested += async (_, _) => await CloseTabAsync(id, _lifetime.Token);
            return id;
        }
        catch
        {
            _ids.Remove(page);
            _tabs.Items.Remove(page);
            view.Dispose();
            throw;
        }
    }
    public Task SwitchTabAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _tabs.SelectedItem = TabFor(id) ?? throw new InvalidOperationException("Tab no longer exists.");
        return Task.CompletedTask;
    }
    public Task CloseTabAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_views.Remove(id, out var view))
        {
            if (_dialogs.Remove(id, out var dialog)) dialog.Deferral.Complete();
            if (TabFor(id) is { } page) { _ids.Remove(page); _tabs.Items.Remove(page); }
            view.Dispose();
        }
        return Task.CompletedTask;
    }
    private TabItem? TabFor(string id) => _ids.FirstOrDefault(entry => entry.Value == id).Key;
    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        foreach (var view in _views.Values) view.Dispose();
        _views.Clear(); _ids.Clear();
    }
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (Dispatcher.CheckAccess()) Close();
            else Dispatcher.Invoke(Close);
        }
        return ValueTask.CompletedTask;
    }

    private const string ButtonStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Width" Value="30" />
          <Setter Property="Height" Value="28" />
          <Setter Property="Margin" Value="1,0" />
          <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
          <Setter Property="FontSize" Value="12" />
          <Setter Property="Foreground" Value="#C3C3CC" />
          <Setter Property="Cursor" Value="Hand" />
          <Setter Property="Focusable" Value="False" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Surface" Background="Transparent" CornerRadius="6">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Surface" Property="Background" Value="#2E2E36" />
                    <Setter Property="Foreground" Value="White" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private const string AddressStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
          <Setter Property="SelectionBrush" Value="#4C8DFF" />
          <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI" />
        </Style>
        """;

    private const string TabStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
          <Setter Property="Foreground" Value="#B8B8C2" />
          <Setter Property="FontSize" Value="12" />
          <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI" />
          <Setter Property="Padding" Value="12,5" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="TabItem">
                <Border x:Name="Surface" Background="Transparent" CornerRadius="6,6,0,0" Margin="2,2,0,0" Padding="{TemplateBinding Padding}">
                  <ContentPresenter ContentSource="Header" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Surface" Property="Background" Value="#26262D" />
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Surface" Property="Background" Value="#33333C" />
                    <Setter Property="Foreground" Value="White" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;
}
