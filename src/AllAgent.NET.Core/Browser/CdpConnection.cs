using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Browser;

/// <summary>Multiplexed CDP connection. Disposing detaches; it never sends Browser.close.</summary>
public sealed class CdpConnection : IBrowserConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reader;
    private int _id;
    private string? _sessionId;
    private string? _browserContext;
    private readonly ConcurrentDictionary<string, string> _sessions = new();
    private readonly ConcurrentDictionary<string, Task<string>> _attaching = new();
    private readonly ConcurrentDictionary<string, JsonElement> _dialogs = new();
    private volatile bool _readerEnded;
    private int _disposed;
    public void Abort() => _socket.Abort();
    public bool IsConnected => !_readerEnded && _socket.State == WebSocketState.Open;
    public bool AwaitingTabAuthorization => _attaching.Values.Any(task => !task.IsCompleted);
    public string BrowserProduct { get; private set; } = "Chromium";
    public string Endpoint { get; private set; } = "";
    public string ActiveTabId { get; private set; } = "";
    public JsonElement? Dialog { get; private set; }
    private TaskCompletionSource _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<CdpConnection> ConnectAsync(string endpoint, CancellationToken ct, string? initialTabId = null, string? preferredUrl = null)
    {
        BrowserUseState.Validate(new(CdpUrl: endpoint));
        var uri = new Uri(endpoint);
        if (uri.Scheme == "http")
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var version = JsonDocument.Parse(await http.GetStringAsync(endpoint.TrimEnd('/') + "/json/version", ct));
            endpoint = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
            BrowserUseState.Validate(new(CdpUrl: endpoint));
            uri = new Uri(endpoint);
        }
        if (!uri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal)) throw new InvalidOperationException("Use the browser CDP endpoint, not a page endpoint. Run /browser connect.");
        var connection = new CdpConnection();
        connection.Endpoint = endpoint;
        try
        {
            connection._socket.Options.Proxy = null;
            await connection._socket.ConnectAsync(uri, ct);
            connection._reader = connection.ReadAsync();
            var version = await connection.CommandAsync("Browser.getVersion", new { }, null, ct);
            connection.BrowserProduct = version.GetProperty("product").GetString() ?? "Chromium";
            var tabs = await connection.TabsAsync(ct);
            if (!string.IsNullOrEmpty(initialTabId) || preferredUrl != null)
            {
                var selected = tabs.FirstOrDefault(t => preferredUrl != null ? t.Url == preferredUrl : t.TabId == initialTabId);
                // A just-launched URL can arrive after the debug socket is available.
                // Wait on this authorized connection rather than repeatedly reconnecting.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (selected == null && preferredUrl != null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200, ct);
                    tabs = await connection.TabsAsync(ct);
                    selected = tabs.FirstOrDefault(t => t.Url == preferredUrl);
                }
                if (selected == null) throw new InvalidOperationException("所选浏览器配置的连接标签页已关闭或尚未就绪，请通过 /browser 重新连接；不会切换到其他配置。");
                var targets = await connection.CommandAsync("Target.getTargets", new { }, null, ct);
                var target = targets.GetProperty("targetInfos").EnumerateArray().First(t => t.GetProperty("targetId").GetString() == selected.TabId);
                if (target.TryGetProperty("browserContextId", out var context)) connection._browserContext = context.GetString();
                await connection.SwitchTabAsync(selected.TabId, ct);
            }
            else if (tabs.Count == 0) await connection.NewTabAsync(ct);
            else await connection.SwitchTabAsync(tabs[0].TabId, ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken ct)
    {
        var pending = CommandAsync(method, parameters, _sessionId, ct);
        if (method is "Runtime.evaluate" or "Runtime.releaseObjectGroup" && await Task.WhenAny(pending, _dialogOpened.Task).WaitAsync(ct) != pending)
        {
            _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return method == "Runtime.releaseObjectGroup" ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { result = new { value = new { interruptedByDialog = true, dialog = Dialog } } });
        }
        return await pending;
    }
    private async Task<JsonElement> CommandAsync(string method, object parameters, string? session, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var promise = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = promise;
        try
        {
            var request = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters };
            if (session != null) request["sessionId"] = session;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            await _send.WaitAsync(ct);
            try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
            finally { _send.Release(); }
            return await promise.Task.WaitAsync(ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReadAsync()
    {
        var buffer = new byte[65536];
        Exception failure = new IOException("Browser disconnected.");
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, frame.Count);
                    if (message.Length > 32 * 1024 * 1024) throw new IOException("CDP message exceeds 32 MiB.");
                } while (!frame.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                if (root.TryGetProperty("method", out var eventName) && root.TryGetProperty("sessionId", out var eventSession))
                {
                    var session = eventSession.GetString()!;
                    if (eventName.GetString() == "Page.javascriptDialogOpening")
                    {
                        _dialogs[session] = root.GetProperty("params").Clone();
                        if (session == _sessionId) { Dialog = _dialogs[session]; _dialogOpened.TrySetResult(); }
                    }
                    else if (eventName.GetString() == "Page.javascriptDialogClosed")
                    {
                        _dialogs.TryRemove(session, out _);
                        if (session == _sessionId) { Dialog = null; _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                    }
                }
                if (!root.TryGetProperty("id", out var id) || !_pending.TryGetValue(id.GetInt32(), out var promise)) continue;
                if (root.TryGetProperty("error", out var error)) promise.TrySetException(new InvalidOperationException("CDP: " + error));
                else promise.TrySetResult(root.GetProperty("result").Clone());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or OperationCanceledException or ObjectDisposedException) { failure = ex; }
        finally { _readerEnded = true; foreach (var promise in _pending.Values) promise.TrySetException(failure); }
    }
    public async Task<IReadOnlyList<BrowserTab>> TabsAsync(CancellationToken ct)
    {
        var result = await CommandAsync("Target.getTargets", new { }, null, ct);
        return result.GetProperty("targetInfos").EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page" &&
                (_browserContext == null || t.TryGetProperty("browserContextId", out var context) && context.GetString() == _browserContext))
            .Select(t => new BrowserTab(t.GetProperty("targetId").GetString()!, t.GetProperty("url").GetString()!, t.GetProperty("title").GetString()!)).ToArray();
    }
    public async Task<string> NewTabAsync(CancellationToken ct)
    {
        var parameters = new Dictionary<string, object> { ["url"] = "about:blank" };
        if (_browserContext != null) parameters["browserContextId"] = _browserContext;
        var result = await CommandAsync("Target.createTarget", parameters, null, ct);
        var id = result.GetProperty("targetId").GetString()!;
        await SwitchTabAsync(id, ct); return id;
    }
    public async Task SwitchTabAsync(string id, CancellationToken ct)
    {
        if (!(await TabsAsync(ct)).Any(t => t.TabId == id)) throw new InvalidOperationException("Tab no longer exists.");
        var attached = !_sessions.TryGetValue(id, out var session);
        if (attached)
        {
            // A caller timeout must not cancel and reissue an attachment that may
            // still be waiting for the browser's consent dialog.
            var pending = _attaching.GetOrAdd(id, target => AttachAsync(target));
            try { session = await pending.WaitAsync(ct); }
            catch when (pending.IsCompleted) { _attaching.TryRemove(id, out _); throw; }
        }
        _sessionId = session; ActiveTabId = id;
        Dialog = _dialogs.TryGetValue(session!, out var dialog) ? dialog : null;
        _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Dialog != null) _dialogOpened.TrySetResult();
        await CommandAsync("Target.activateTarget", new { targetId = id }, null, ct);
    }
    public async Task CloseTabAsync(string id, CancellationToken ct)
    {
        await CommandAsync("Target.closeTarget", new { targetId = id }, null, ct);
        _attaching.TryRemove(id, out _);
        if (_sessions.TryRemove(id, out var session)) _dialogs.TryRemove(session, out _);
        if (id != ActiveTabId) return;
        _sessionId = null; ActiveTabId = "";
        var remaining = await TabsAsync(ct);
        if (remaining.Count > 0) await SwitchTabAsync(remaining[0].TabId, ct);
    }
    private async Task<string> AttachAsync(string target)
    {
        var result = await CommandAsync("Target.attachToTarget", new { targetId = target, flatten = true }, null, _lifetime.Token);
        var session = result.GetProperty("sessionId").GetString()!;
        await CommandAsync("Page.enable", new { }, session, _lifetime.Token);
        _sessions[target] = session;
        return session;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _socket.Abort();
        if (_reader != null) await _reader;
        _socket.Dispose(); _lifetime.Dispose();
    }
}
