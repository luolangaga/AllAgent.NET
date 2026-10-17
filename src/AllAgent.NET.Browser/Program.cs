using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Browser;
using AllAgent.NET.Core.Tools;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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
        ApplicationConfiguration.Initialize();
        var profile = args.Length == 2 && args[0] == "--profile" ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllAgent.NET", "browser-use");
        using var window = new BrowserWindow(profile);
        Application.Run(window);
    }
}

internal sealed class BrowserWindow(string profile) : Form, IBrowserConnection
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStripTextBox _address = new() { AutoSize = false, Width = 650 };
    private readonly Dictionary<string, WebView2> _views = [];
    private CoreWebView2Environment? _environment;
    private readonly CancellationTokenSource _lifetime = new();
    private BrowserAutomationSession? _session;
    private bool _disposed;
    private readonly Dictionary<string, (CoreWebView2ScriptDialogOpeningEventArgs Args, CoreWebView2Deferral Deferral)> _dialogs = [];
    private TaskCompletionSource _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public JsonElement? Dialog => _dialogs.TryGetValue(ActiveTabId, out var pending)
        ? JsonSerializer.SerializeToElement(new { type = pending.Args.Kind.ToString(), message = pending.Args.Message, defaultPrompt = pending.Args.DefaultText }) : null;
    public string ActiveTabId => _tabs.SelectedTab?.Name ?? "";
    private WebView2 Current => _views.TryGetValue(ActiveTabId, out var view) ? view : throw new InvalidOperationException("No active tab. Open a new tab.");

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Text = "AllAgent Browser · WebView2"; Width = 1180; Height = 820;
        var toolbar = new ToolStrip { Dock = DockStyle.Top };
        toolbar.Items.Add("←", null, (_, _) => { if (Current.CanGoBack) Current.GoBack(); });
        toolbar.Items.Add("→", null, (_, _) => { if (Current.CanGoForward) Current.GoForward(); });
        toolbar.Items.Add("↻", null, (_, _) => Current.Reload());
        toolbar.Items.Add(_address);
        toolbar.Items.Add("打开", null, (_, _) => NavigateAddress());
        toolbar.Items.Add("+", null, async (_, _) => { try { await NewTabAsync(_lifetime.Token); } catch (Exception ex) { _address.Text = ex.Message; } });
        toolbar.Items.Add("关闭标签", null, async (_, _) => await CloseTabAsync(ActiveTabId, _lifetime.Token));
        _address.KeyDown += (_, key) => { if (key.KeyCode == Keys.Enter) { key.SuppressKeyPress = true; NavigateAddress(); } };
        Controls.Add(_tabs); Controls.Add(toolbar);
        _tabs.SelectedIndexChanged += (_, _) => { if (_views.TryGetValue(ActiveTabId, out var view)) _address.Text = view.Source?.ToString() ?? "about:blank"; };
        try
        {
            Directory.CreateDirectory(profile);
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(profile, "webview2"));
            await NewTabAsync(_lifetime.Token);
            _session = new BrowserAutomationSession(this);
            while (!_lifetime.IsCancellationRequested)
            {
                // Standard input may be a synchronous Windows pipe despite the async
                // API. Never enter its read on the WinForms message-pump thread.
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
        var tab = new TabPage("新标签") { Name = id };
        var view = new WebView2 { Dock = DockStyle.Fill };
        tab.Controls.Add(view); _tabs.TabPages.Add(tab); _tabs.SelectedTab = tab;
        try
        {
            await view.EnsureCoreWebView2Async(_environment).WaitAsync(ct);
            _views.Add(id, view);
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            view.CoreWebView2.ScriptDialogOpening += (_, dialog) => { _dialogs[id] = (dialog, dialog.GetDeferral()); _dialogOpened.TrySetResult(); };
            view.CoreWebView2.DocumentTitleChanged += (_, _) => { tab.Text = view.CoreWebView2.DocumentTitle[..Math.Min(35, view.CoreWebView2.DocumentTitle.Length)]; };
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
        catch { _tabs.TabPages.Remove(tab); view.Dispose(); tab.Dispose(); throw; }
    }
    public Task SwitchTabAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Name == id) ?? throw new InvalidOperationException("Tab no longer exists.");
        return Task.CompletedTask;
    }
    public Task CloseTabAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_views.Remove(id, out var view))
        {
            if (_dialogs.Remove(id, out var dialog)) dialog.Deferral.Complete();
            var tab = _tabs.TabPages.Cast<TabPage>().First(t => t.Name == id);
            _tabs.TabPages.Remove(tab); view.Dispose(); tab.Dispose();
        }
        return Task.CompletedTask;
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    { _lifetime.Cancel(); foreach (var view in _views.Values) view.Dispose(); _views.Clear(); base.OnFormClosed(e); }
    public ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; Close(); } return ValueTask.CompletedTask; }
}
