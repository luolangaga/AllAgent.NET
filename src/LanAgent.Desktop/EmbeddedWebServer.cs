using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanAgent.Desktop;

/// <summary>
/// Serves embedded Avalonia web assets over HTTP so the WebView can load them.
/// Picks a random free port and serves avares://LanAgent.Desktop/Assets/web/
/// </summary>
public sealed class EmbeddedWebServer : IDisposable
{
    private WebApplication? _app;
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.WebHost.SuppressStatusMessages(true);

        _app = builder.Build();

        _app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            var path = ctx.Request.Path.Value?.TrimStart('/') ?? "";
            if (string.IsNullOrEmpty(path)) path = "index.html";

            var resourceUri = new Uri($"avares://LanAgent.Desktop/Assets/web/{path}");
            try
            {
                var stream = Avalonia.Platform.AssetLoader.Open(resourceUri);
                ctx.Response.ContentType = GetMimeType(path);
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            catch
            {
                // SPA fallback: return index.html for unknown paths
                try
                {
                    var fallback = Avalonia.Platform.AssetLoader.Open(
                        new Uri("avares://LanAgent.Desktop/Assets/web/index.html"));
                    ctx.Response.ContentType = "text/html";
                    await fallback.CopyToAsync(ctx.Response.Body, ct);
                }
                catch
                {
                    ctx.Response.StatusCode = 404;
                }
            }
        });

        await _app.StartAsync(ct);
    }

    public void Dispose() => _app?.DisposeAsync().AsTask().Wait(2000);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js"   => "application/javascript",
        ".css"  => "text/css",
        ".json" => "application/json",
        ".svg"  => "image/svg+xml",
        ".png"  => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".woff2" => "font/woff2",
        ".woff"  => "font/woff",
        ".ico"   => "image/x-icon",
        _        => "application/octet-stream",
    };
}
