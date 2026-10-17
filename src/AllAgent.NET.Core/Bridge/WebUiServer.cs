using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AllAgent.NET.Core.Bridge;

/// <summary>
/// Serves the built web workspace over loopback HTTP and bridges browser
/// requests through the same dispatcher/event pusher the desktop WebView
/// uses. The transport is a plain HttpListener + WebSocket pair so a host
/// using it (the terminal's /web-ui) needs no ASP.NET Core runtime.
/// </summary>
public sealed class WebUiServer : IAsyncDisposable
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".mjs"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _webRoot;
    private Task? _acceptLoop;
    private static int _exceptionHandlersInstalled;

    public int Port { get; }
    public string Url { get; }
    public int ConnectedClients => _clients.Count;
    /// <summary>Raised after a bridge client connects; returns how many prompts a reloaded page had missed.</summary>
    public Func<int>? ClientConnected { get; set; }

    private WebUiServer(BridgeDispatcher dispatcher, BridgeEventPusher pusher, string webRoot, int port)
    {
        _webRoot = Path.GetFullPath(webRoot);
        Port = port != 0 ? port : GetFreePort();
        Url = $"http://127.0.0.1:{Port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        Log($"[WebUiServer] listening on {Url} (root {_webRoot})");
        pusher.SetPusher(Broadcast);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(dispatcher, _cts.Token));
    }

    public static WebUiServer Start(BridgeDispatcher dispatcher, BridgeEventPusher pusher, string webRoot, int port = 0)
    {
        // A crash in a background thread used to end the host silently; log the full
        // stack so the next failure is diagnosable.
        if (Interlocked.Exchange(ref _exceptionHandlersInstalled, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log($"[WebUiServer] UNHANDLED {args.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log($"[WebUiServer] unobserved task exception: {args.Exception}");
                args.SetObserved();
            };
        }
        try { return new WebUiServer(dispatcher, pusher, webRoot, port); }
        // A requested port can be taken by another process between sessions; fall back to a free one.
        catch (Exception) when (port != 0) { return new WebUiServer(dispatcher, pusher, webRoot, 0); }
    }

    private async Task AcceptLoopAsync(BridgeDispatcher dispatcher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            _ = Task.Run(() => HandleAsync(context, dispatcher, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, BridgeDispatcher dispatcher, CancellationToken ct)
    {
        try
        {
            // Loopback only, and only under the host name we published: any
            // other Host header is a DNS-rebinding attempt, not a browser tab.
            if (!IsLoopbackHost(context.Request))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }
            if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/bridge")
            {
                await AcceptBridgeAsync(context, dispatcher, ct);
                return;
            }
            await ServeAssetAsync(context, ct);
        }
        catch (Exception ex)
        {
            Log($"[WebUiServer] request failed: {ex.Message}");
            try { context.Response.Abort(); } catch { /* connection already gone */ }
        }
    }

    private static bool IsLoopbackHost(HttpListenerRequest request)
    {
        var host = request.Url?.Host ?? "";
        return host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    private async Task AcceptBridgeAsync(HttpListenerContext context, BridgeDispatcher dispatcher, CancellationToken ct)
    {
        // A web page on another origin must not get access to local host tools.
        var origin = context.Request.Headers["Origin"];
        if (origin != null && (!Uri.TryCreate(origin, UriKind.Absolute, out var source)
            || source.GetLeftPart(UriPartial.Authority) != context.Request.Url!.GetLeftPart(UriPartial.Authority)))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }
        var socketContext = await context.AcceptWebSocketAsync(null);
        var client = new Client(socketContext.WebSocket);
        var id = Guid.NewGuid();
        _clients[id] = client;
        Log($"[WebUiServer] bridge client connected ({_clients.Count} active)");
        try
        {
            var replayed = ClientConnected?.Invoke() ?? 0;
            if (replayed > 0) Log($"[WebUiServer] replayed {replayed} pending prompt(s) to the new client");
        }
        catch (Exception ex) { Log($"[WebUiServer] client-connected hook failed: {ex.Message}"); }
        var buffer = new byte[64 * 1024];
        using var payload = new MemoryStream();
        try
        {
            while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                payload.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text || payload.Length + result.Count > 16 * 1024 * 1024)
                    {
                        await client.Socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "Expected bounded JSON text", ct);
                        return;
                    }
                    payload.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // Decode once: ReceiveAsync may split a Chinese character or emoji
                // anywhere between UTF-8 bytes, even within a single frame.
                var request = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
                if (request.Length == 0) continue;
                // Replies are correlated by request id, so parallel handlers stay safe.
                _ = Task.Run(async () =>
                {
                    var response = await dispatcher.InvokeAsync(request, ct);
                    await SendAsync(client, response, ct);
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            try { client.Socket.Dispose(); } catch { /* already closed */ }
            Log($"[WebUiServer] bridge client disconnected ({_clients.Count} active)");
        }
    }

    private static async Task SendAsync(Client client, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try { await client.SendGate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (client.Socket.State != WebSocketState.Open) return;
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally { client.SendGate.Release(); }
    }

    private void Broadcast(string json)
    {
        foreach (var client in _clients.Values) _ = SendAsync(client, json, _cts.Token);
    }

    private async Task ServeAssetAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
        if (string.IsNullOrEmpty(path)) path = "index.html";
        var indexPath = Path.Combine(_webRoot, "index.html");
        var candidate = Path.GetFullPath(Path.Combine(_webRoot, path));
        var insideRoot = candidate.StartsWith(_webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string? file = insideRoot && File.Exists(candidate) ? candidate : null;
        // SPA fallback only for route-like paths; a missing .js/.css is a real 404.
        if (file == null && Path.GetExtension(path).Length == 0 && File.Exists(indexPath)) file = indexPath;
        if (file == null)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }
        var isIndex = string.Equals(file, indexPath, StringComparison.OrdinalIgnoreCase);
        context.Response.StatusCode = 200;
        context.Response.ContentType = MimeTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        // Entry scripts currently have stable names (assets/index.js). Caching
        // those across host upgrades mixes a new bridge with an old frontend.
        var extension = Path.GetExtension(file).ToLowerInvariant();
        context.Response.Headers["Cache-Control"] = isIndex || extension is ".js" or ".mjs" or ".css" or ".json" ? "no-cache" : "public, max-age=3600";
        var bytes = await File.ReadAllBytesAsync(file, ct);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* already stopped */ }
        foreach (var client in _clients.Values)
        {
            try { client.Socket.Abort(); } catch { /* already gone */ }
        }
        _clients.Clear();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* loop ends with the listener */ }
        }
        _cts.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Log(string message)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logPath = Path.Combine(appData, "AllAgent.NET", "allagent.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash */ }
    }
}
