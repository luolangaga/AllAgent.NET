using System.Net;
using System.Runtime.InteropServices;

namespace LanAgent.Core.Services.AI;

internal static class ModelHttpClient
{
    internal static HttpClient Create(TimeSpan? timeout = null) => new(CreateHandler())
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(100)
    };

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        Proxy = OperatingSystem.IsWindows() ? new CurrentWindowsProxy() : null
    };
}

/// <summary>Refresh manual Windows proxy settings for each connection decision.</summary>
internal sealed class CurrentWindowsProxy : IWebProxy
{
    private readonly Func<Uri, IWebProxy?> _resolve;
    internal CurrentWindowsProxy(Func<Uri, IWebProxy?>? resolve = null) => _resolve = resolve ?? Resolve;
    public ICredentials? Credentials { get; set; }
    public Uri? GetProxy(Uri destination) => _resolve(destination)?.GetProxy(destination) ?? destination;
    public bool IsBypassed(Uri host) => _resolve(host)?.IsBypassed(host) ?? true;

    private static IWebProxy? Resolve(Uri destination)
    {
        // Explicit environment proxies retain .NET's precedence and NO_PROXY behavior.
        if (new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }.Any(name =>
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            return HttpClient.DefaultProxy;

        if (!WinHttpGetIEProxyConfigForCurrentUser(out var config))
            return HttpClient.DefaultProxy;
        try
        {
            // Keep platform PAC/WPAD handling; this refresh concerns manual proxies.
            if (config.AutoDetect || config.AutoConfigUrl != IntPtr.Zero)
                return HttpClient.DefaultProxy;
            var server = Marshal.PtrToStringUni(config.Proxy);
            if (string.IsNullOrWhiteSpace(server)) return null;
            var entries = server.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var address = entries.FirstOrDefault(entry => !entry.Contains('='));
            foreach (var entry in entries)
            {
                var pair = entry.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals(destination.Scheme, StringComparison.OrdinalIgnoreCase))
                    address = pair[1];
            }
            if (address is null) return null;
            var proxy = new WebProxy(address.Contains("://") ? address : "http://" + address);
            var bypass = Marshal.PtrToStringUni(config.ProxyBypass);
            if (!string.IsNullOrWhiteSpace(bypass))
            {
                var patterns = new List<string>();
                foreach (var entry in bypass.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase)) proxy.BypassProxyOnLocal = true;
                    else patterns.Add("^(?:.*://)?" + System.Text.RegularExpressions.Regex.Escape(entry).Replace("\\*", ".*").Replace("\\?", ".") + "(?::[0-9]+)?$");
                }
                proxy.BypassList = patterns.ToArray();
            }
            return proxy;
        }
        finally
        {
            if (config.AutoConfigUrl != IntPtr.Zero) GlobalFree(config.AutoConfigUrl);
            if (config.Proxy != IntPtr.Zero) GlobalFree(config.Proxy);
            if (config.ProxyBypass != IntPtr.Zero) GlobalFree(config.ProxyBypass);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect;
        public IntPtr AutoConfigUrl;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out ProxyConfig config);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
