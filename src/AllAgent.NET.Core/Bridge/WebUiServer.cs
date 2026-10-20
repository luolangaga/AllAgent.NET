using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AllAgent.NET.Core.Bridge;

/// <summary>Listener address and the access control shared with the settings page.</summary>
/// <param name="LocalAccessFree">Requests from this machine skip the LAN gate. Tests turn it off
/// to exercise the remote path from a loopback client.</param>
public sealed record WebUiServerOptions(bool Lan = false, int Port = 0, WebUiAccessControl? Access = null, bool LocalAccessFree = true);

/// <summary>
/// Serves the built web workspace over HTTP and bridges browser requests through the same
/// dispatcher/event pusher the desktop WebView uses. A plain TcpListener carries the HTTP and
/// WebSocket traffic, so binding a LAN address does not need an HTTP.sys URL reservation.
/// </summary>
public sealed class WebUiServer : IAsyncDisposable
{
    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body,
        string RemoteAddress)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string Host => Header("Host") ?? "";
        public string? Origin => Header("Origin");
        public bool HasBody => Body.Length > 0;
        public bool IsWebSocketUpgrade => Method == "GET"
            && Header("Upgrade") is { } upgrade && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && Header("Connection") is { } connection && connection.Split(',').Any(part => part.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            && Header("Sec-WebSocket-Key") is { Length: > 0 }
            && Header("Sec-WebSocket-Version") == "13";
    }

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _webRoot;
    private readonly bool _lan;
    private readonly bool _localAccessFree;
    private readonly WebUiAccessControl? _access;
    private readonly Dictionary<string, (int Count, DateTime Since)> _loginFailures = new(StringComparer.Ordinal);
    private Task? _acceptLoop;
    private static int _exceptionHandlersInstalled;

    public int Port { get; }
    public string LocalUrl => $"http://127.0.0.1:{Port}/";
    public string Url { get; }
    public IReadOnlyList<string> Urls { get; }
    public bool Lan => _lan;
    /// <summary>LAN listeners gate other devices; requests from this machine stay password-free.</summary>
    public bool AuthenticationRequired => _lan;
    public WebUiAccessControl? Access => _access;
    public int ConnectedClients => _clients.Count;
    /// <summary>Raised after a bridge client connects; returns how many prompts a reloaded page had missed.</summary>
    public Func<int>? ClientConnected { get; set; }

    /// <summary>Tokenized URL for another device. Local URLs never carry the token.</summary>
    public string AccessUrl(string baseUrl)
    {
        if (!_lan || _access == null) return baseUrl;
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "access=" + Uri.EscapeDataString(_access.AccessToken);
    }

    private WebUiServer(BridgeDispatcher dispatcher, BridgeEventPusher pusher, string webRoot, WebUiServerOptions options)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _lan = options.Lan;
        _localAccessFree = options.LocalAccessFree;
        _access = options.Access;
        var requested = options.Port is > 0 and <= 65535 ? options.Port : 0;
        var bind = _lan ? IPAddress.Any : IPAddress.Loopback;
        _listener = new TcpListener(bind, requested);
        try { _listener.Start(); }
        // A requested port can be taken by another process between sessions; fall back to a free one.
        catch (SocketException) when (requested != 0)
        {
            _listener = new TcpListener(bind, 0);
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var lanUrls = _lan ? WebUiAddress.Urls(Port) : [];
        Urls = lanUrls.Count > 0 ? lanUrls : [LocalUrl];
        Url = lanUrls.Count > 0 ? lanUrls[0] : LocalUrl;
        Log($"[WebUiServer] listening on {(_lan ? string.Join(", ", lanUrls) : LocalUrl)} (root {_webRoot})");
        pusher.SetPusher(Broadcast);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(dispatcher, _cts.Token));
    }

    public static WebUiServer Start(BridgeDispatcher dispatcher, BridgeEventPusher pusher, string webRoot, int port = 0)
        => Start(dispatcher, pusher, webRoot, new WebUiServerOptions(false, port));

    public static WebUiServer Start(BridgeDispatcher dispatcher, BridgeEventPusher pusher, string webRoot, WebUiServerOptions options)
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
        return new WebUiServer(dispatcher, pusher, webRoot, options);
    }

    private async Task AcceptLoopAsync(BridgeDispatcher dispatcher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleConnectionAsync(client, dispatcher, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, BridgeDispatcher dispatcher, CancellationToken ct)
    {
        using var connection = tcp;
        connection.NoDelay = true;
        var remote = (connection.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        try
        {
            // A raw NetworkStream is required: ManagedWebSocket writes frames without an
            // explicit flush, so wrapping the transport in a BufferedStream would swallow them.
            var stream = connection.GetStream();
            var request = await ReadRequestAsync(stream, remote, ct);
            if (request == null) return;
            if (request.Path is "/" or "/__auth/status" or "/__auth/login" or "/bridge")
                Log($"[WebUiServer] {request.Method} {request.Path} remote={remote} host={request.Host}");
            if (!WebUiAddress.IsLocalHost(request.Host, _lan))
            {
                // Only the host name we published may reach the app; anything else is a
                // DNS-rebinding attempt, not a browser tab.
                Log($"[WebUiServer] blocked host '{request.Host}' from {remote}");
                await WriteAsync(stream, 403, "text/plain; charset=utf-8", "Forbidden"u8.ToArray(), ct: ct);
                return;
            }
            if (request.Path == "/__auth/status")
            {
                var required = RequiresAuthentication(request);
                var authenticated = Authenticated(request);
                Log($"[WebUiServer] auth status remote={remote} required={required} authenticated={authenticated} password={_access?.PasswordConfigured == true}");
                await WriteJsonAsync(stream, 200, new
                {
                    required,
                    authenticated,
                    lan = _lan,
                    passwordConfigured = _access?.PasswordConfigured == true,
                    urls = Urls,
                    localUrl = LocalUrl,
                }, ct);
                return;
            }
            if (request.Path == "/__auth/login")
            {
                await HandleLoginAsync(stream, request, remote, ct);
                return;
            }
            if (request.Path == "/bridge")
            {
                if (!request.IsWebSocketUpgrade)
                {
                    await WriteAsync(stream, 400, "text/plain; charset=utf-8", "Expected a WebSocket upgrade"u8.ToArray(), ct: ct);
                    return;
                }
                if (!Authenticated(request))
                {
                    Log($"[WebUiServer] bridge unauthorized remote={remote} host={request.Host}");
                    await WriteAsync(stream, 401, "text/plain; charset=utf-8", "Unauthorized"u8.ToArray(), ct: ct);
                    return;
                }
                if (!OriginAllowed(request))
                {
                    // A web page on another origin must not get access to local host tools.
                    Log($"[WebUiServer] bridge origin rejected remote={remote} host={request.Host} origin={request.Origin}");
                    await WriteAsync(stream, 403, "text/plain; charset=utf-8", "Forbidden"u8.ToArray(), ct: ct);
                    return;
                }
                await AcceptBridgeAsync(stream, request, dispatcher, ct);
                return;
            }
            await ServeAssetAsync(stream, request, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Log($"[WebUiServer] request failed: {ex.Message}");
            try { connection.Client.Close(); } catch { /* connection already gone */ }
        }
    }

    private async Task<HttpRequest?> ReadRequestAsync(Stream stream, string remote, CancellationToken ct)
    {
        var head = await ReadHeadAsync(stream, ct);
        if (head == null) return null;
        var text = Encoding.UTF8.GetString(head);
        var lines = text.TrimEnd('\r', '\n').Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var first = lines[0].Split(' ');
        if (first.Length < 3 || !first[2].StartsWith("HTTP/", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var previous) ? previous + "," + value : value;
        }
        var target = first[1];
        if (!target.StartsWith('/')) return null;
        var question = target.IndexOf('?');
        var path = Uri.UnescapeDataString(question < 0 ? target : target[..question]);
        var query = question < 0 ? "" : target[(question + 1)..];
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var lengthHeader))
        {
            if (!int.TryParse(lengthHeader, out length) || length < 0 || length > 64 * 1024) return null;
        }
        if (headers.ContainsKey("Transfer-Encoding")) return null;
        var body = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(body, ct);
        return new HttpRequest(first[0], path, query, headers, body, remote);
    }

    private static async Task<byte[]?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        const int limit = 32 * 1024;
        using var data = new MemoryStream();
        var one = new byte[1];
        var state = 0;
        while (data.Length < limit)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0) return null;
            data.WriteByte(one[0]);
            if (one[0] == (byte)'\r') state = state is 0 or 2 ? state + 1 : 1;
            else if (one[0] == (byte)'\n') state = state is 1 or 3 ? state + 1 : 0;
            else state = 0;
            if (state == 4) return data.ToArray();
        }
        return null;
    }

    /// <summary>Requests from this machine always pass; other devices need the QR token or a password.</summary>
    private bool Authenticated(HttpRequest request)
    {
        if (!RequiresAuthentication(request)) return true;
        var token = QueryValue(request.Query, "access") ?? request.Header("X-AllAgent-Access");
        return _access?.VerifyToken(token) == true;
    }

    private bool RequiresAuthentication(HttpRequest request) => _lan && !IsLocalRequest(request);

    private bool IsLocalRequest(HttpRequest request) => _localAccessFree
        && IPAddress.TryParse(request.RemoteAddress, out var address) && WebUiAddress.IsLocalAddress(address);

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = equals < 0 ? pair : pair[..equals];
            if (!name.Equals(key, StringComparison.Ordinal)) continue;
            return Uri.UnescapeDataString((equals < 0 ? "" : pair[(equals + 1)..]).Replace('+', ' '));
        }
        return null;
    }

    private bool OriginAllowed(HttpRequest request)
    {
        var origin = request.Origin;
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var source)) return false;
        var expected = "http://" + request.Host;
        return source.GetLeftPart(UriPartial.Authority).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleLoginAsync(Stream stream, HttpRequest request, string remote, CancellationToken ct)
    {
        // This machine never needs a login; the local window and terminal browser open directly.
        if (!RequiresAuthentication(request))
        {
            await WriteJsonAsync(stream, 200, new { token = (string?)null }, ct);
            return;
        }
        if (request.Method != "POST")
        {
            await WriteAsync(stream, 405, "text/plain; charset=utf-8", "Method not allowed"u8.ToArray(), ct: ct);
            return;
        }
        if (_access?.PasswordConfigured != true)
        {
            await WriteJsonAsync(stream, 409, new { error = "尚未设置访问密码。请在宿主机的 Web 设置中设置密码，或用二维码扫码进入。", code = "password-not-set" }, ct);
            return;
        }
        if (IsRateLimited(remote))
        {
            await WriteJsonAsync(stream, 429, new { error = "尝试次数过多，请稍后再试。" }, ct);
            return;
        }
        string? password = null;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            if (json.RootElement.TryGetProperty("password", out var value)) password = value.GetString();
        }
        catch (JsonException) { /* treated as an empty password */ }
        if (_access.VerifyPassword(password ?? "") != true)
        {
            RecordFailure(remote);
            await WriteJsonAsync(stream, 401, new { error = "密码不正确。" }, ct);
            return;
        }
        lock (_loginFailures) _loginFailures.Remove(remote);
        await WriteJsonAsync(stream, 200, new { token = _access.AccessToken }, ct);
    }

    private bool IsRateLimited(string remote)
    {
        lock (_loginFailures)
        {
            if (!_loginFailures.TryGetValue(remote, out var entry)) return false;
            if (DateTime.UtcNow - entry.Since > TimeSpan.FromMinutes(1))
            {
                _loginFailures.Remove(remote);
                return false;
            }
            return entry.Count >= 8;
        }
    }

    private void RecordFailure(string remote)
    {
        lock (_loginFailures)
        {
            if (!_loginFailures.TryGetValue(remote, out var entry) || DateTime.UtcNow - entry.Since > TimeSpan.FromMinutes(1))
                _loginFailures[remote] = (1, DateTime.UtcNow);
            else
                _loginFailures[remote] = (entry.Count + 1, entry.Since);
        }
    }

    private async Task AcceptBridgeAsync(Stream stream, HttpRequest request, BridgeDispatcher dispatcher, CancellationToken ct)
    {
        var key = request.Header("Sec-WebSocket-Key")!;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await stream.FlushAsync(ct);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        var client = new Client(socket);
        var id = Guid.NewGuid();
        _clients[id] = client;
        Log($"[WebUiServer] bridge client connected ({_clients.Count} active) remote={request.RemoteAddress} host={request.Host} origin={request.Origin ?? "-"}");
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
                var text = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
                if (text.Length == 0) continue;
                // Replies are correlated by request id, so parallel handlers stay safe.
                _ = Task.Run(async () =>
                {
                    var response = await dispatcher.InvokeAsync(text, ct);
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

    private async Task ServeAssetAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteAsync(stream, 405, "text/plain; charset=utf-8", "Method not allowed"u8.ToArray(), ct: ct);
            return;
        }
        var path = request.Path.TrimStart('/');
        if (string.IsNullOrEmpty(path)) path = "index.html";
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(_webRoot, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await WriteAsync(stream, 400, "text/plain; charset=utf-8", "Bad path"u8.ToArray(), ct: ct);
            return;
        }
        var indexPath = Path.Combine(_webRoot, "index.html");
        var insideRoot = candidate.StartsWith(_webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string? file = insideRoot && File.Exists(candidate) ? candidate : null;
        // SPA fallback only for route-like paths; a missing .js/.css is a real 404.
        if (file == null && Path.GetExtension(path).Length == 0 && File.Exists(indexPath)) file = indexPath;
        if (file == null)
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "Not found"u8.ToArray(), ct: ct);
            return;
        }
        var isIndex = string.Equals(file, indexPath, StringComparison.OrdinalIgnoreCase);
        // Entry scripts use stable names (assets/index.js), so a phone that cached them across
        // a host upgrade would run an old bridge. Force a fresh copy of the entry layer; hashed
        // vendor chunks keep their normal revalidation.
        var entryName = Path.GetFileName(file);
        var isEntry = isIndex
            || entryName.Equals("index.js", StringComparison.OrdinalIgnoreCase)
            || entryName.Equals("index.mjs", StringComparison.OrdinalIgnoreCase)
            || entryName.Equals("index.css", StringComparison.OrdinalIgnoreCase);
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var cache = isEntry ? "no-store" : extension is ".js" or ".mjs" or ".css" or ".json" ? "no-cache" : "public, max-age=3600";
        var bytes = await File.ReadAllBytesAsync(file, ct);
        await WriteAsync(stream, 200, LoopbackHttp.ContentTypeFor(file), bytes, new Dictionary<string, string> { ["Cache-Control"] = cache }, request.Method != "HEAD", ct);
    }

    private static async Task WriteJsonAsync(Stream stream, int status, object payload, CancellationToken ct)
        => await WriteAsync(stream, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions.Default), ct: ct);

    private static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body, IReadOnlyDictionary<string, string>? headers = null, bool includeBody = true, CancellationToken ct = default)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("X-Content-Type-Options: nosniff\r\n");
        if (headers != null)
            foreach (var (name, value) in headers) builder.Append(name).Append(": ").Append(value).Append("\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct);
        if (includeBody && body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "Error",
    };

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
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

    private static void Log(string message) => LoopbackHttp.Log(message);
}
