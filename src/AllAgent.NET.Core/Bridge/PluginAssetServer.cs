using System.Net;
using AllAgent.NET.Core.Extensions;

namespace AllAgent.NET.Core.Bridge;

/// <summary>
/// Serves plugin web payloads (the "web/" folder inside an installed package) and the plugin SDK
/// script on its own loopback origin. A frame loaded from here shares an origin with its own
/// assets but not with the app page, so a sandboxed iframe can neither touch the host DOM nor
/// reach the /bridge socket (whose Origin check rejects every other origin).
/// </summary>
public sealed class PluginAssetServer : IAsyncDisposable
{
    private const string SdkPrefix = "/__sdk/";
    private const string PluginPrefix = "/p/";
    private static readonly string[] SdkFiles = ["allagent-plugin-sdk.js", "allagent-plugin-sdk.mjs", "allagent-plugin-ui.mjs"];

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PluginHost _plugins;
    private readonly string _webRoot;
    private Task? _acceptLoop;

    public int Port { get; }
    public string BaseUrl { get; }

    public static PluginAssetServer? TryStart(PluginHost plugins, string webRoot, int port = 0)
    {
        try { return new PluginAssetServer(plugins, webRoot, port); }
        catch (Exception ex) { LoopbackHttp.Log($"[PluginAssetServer] start failed: {ex.Message}"); return null; }
    }

    private PluginAssetServer(PluginHost plugins, string webRoot, int port)
    {
        _plugins = plugins;
        _webRoot = Path.GetFullPath(webRoot);
        Port = port != 0 ? port : LoopbackHttp.GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        LoopbackHttp.Log($"[PluginAssetServer] listening on {BaseUrl} (web root {_webRoot})");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            _ = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (!LoopbackHttp.IsLoopbackHost(context.Request))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }
            var method = context.Request.HttpMethod;
            if (method != "GET" && method != "HEAD")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (path.StartsWith(SdkPrefix, StringComparison.Ordinal))
            {
                var name = path[SdkPrefix.Length..];
                var sdk = SdkFiles.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (sdk == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                await ServeFileAsync(context, Path.Combine(_webRoot, "sdk", sdk), method == "HEAD", ct);
                return;
            }
            if (path.StartsWith(PluginPrefix, StringComparison.Ordinal))
            {
                var rest = path[PluginPrefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                var packageId = Uri.UnescapeDataString(rest[..slash]);
                var relative = Uri.UnescapeDataString(rest[(slash + 1)..]);
                var directory = _plugins.WebAssetRoot(packageId);
                var root = directory == null ? null : Path.GetFullPath(Path.Combine(directory, "web"));
                var candidate = root == null || relative.Length == 0 ? null : Path.GetFullPath(Path.Combine(directory!, relative));
                if (root == null || candidate == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }
                if (!File.Exists(candidate))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                await ServeFileAsync(context, candidate, method == "HEAD", ct);
                return;
            }
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            LoopbackHttp.Log($"[PluginAssetServer] request failed: {ex.Message}");
            try { context.Response.Abort(); } catch { /* connection already gone */ }
        }
    }

    private static async Task ServeFileAsync(HttpListenerContext context, string file, bool headerOnly, CancellationToken ct)
    {
        if (!File.Exists(file))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = LoopbackHttp.ContentTypeFor(file);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Entry documents and scripts have stable names; a no-cache policy keeps a host upgrade
        // from mixing an old plugin UI with a new frame protocol.
        var extension = Path.GetExtension(file).ToLowerInvariant();
        context.Response.Headers["Cache-Control"] = extension is ".html" or ".js" or ".mjs" or ".css" or ".json" ? "no-cache" : "public, max-age=3600";
        if (headerOnly)
        {
            context.Response.Close();
            return;
        }
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
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* loop ends with the listener */ }
        }
        _cts.Dispose();
    }
}
