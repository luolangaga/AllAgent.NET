using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanAgent.Terminal;

/// <summary>A temporary, loopback-only connection page opened in the selected profile.</summary>
internal sealed class BrowserConnectionPage : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    public string Url { get; }

    public BrowserConnectionPage(string kind)
    {
        _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/lanagent-" + Guid.NewGuid().ToString("N");
        _server = ServeAsync(kind == "edge" ? "edge://inspect/#remote-debugging" : "chrome://inspect/#remote-debugging");
    }

    private async Task ServeAsync(string settings)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'><title>LanAgent · 等待连接</title><style>body{font:20px system-ui;max-width:760px;margin:10vh auto;padding:24px;line-height:1.8}</style><h1>正在连接这个浏览器配置</h1><p>请在另一个标签页打开 <code>" + settings + "</code>，启用“允许对此浏览器实例进行远程调试”，并允许授权弹窗。</p><p>保持此页面打开。LanAgent 验证成功后，这里会自动显示“浏览器已连接”。</p>");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(requestTimeout.Token);
                    var valid = request?.StartsWith("GET " + new Uri(Url).AbsolutePath + " ", StringComparison.Ordinal) == true;
                    var response = valid ? body : Encoding.UTF8.GetBytes("Not found");
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (valid ? "200 OK" : "404 Not Found") + "\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + response.Length + "\r\n\r\n");
                    await stream.WriteAsync(header, requestTimeout.Token);
                    await stream.WriteAsync(response, requestTimeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _server; _stop.Dispose();
    }
}
