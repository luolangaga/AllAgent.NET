using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AllAgent.NET.Core.Bridge;

/// <summary>Shared loopback HTTP plumbing for the two host listeners: Host-header guard,
/// free-port probe, MIME map and the log sink.</summary>
internal static class LoopbackHttp
{
    public static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        [".wasm"] = "application/wasm",
        [".webmanifest"] = "application/manifest+json",
        [".txt"] = "text/plain; charset=utf-8",
    };

    public static string ContentTypeFor(string path) => MimeTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    /// <summary>Loopback only, and only under the host name we published: any other Host header is
    /// a DNS-rebinding attempt, not a browser tab.</summary>
    public static bool IsLoopbackHost(HttpListenerRequest request)
    {
        var host = request.Url?.Host ?? "";
        return host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void Log(string message)
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
