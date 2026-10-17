using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Browser;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class BrowserConnectionLifecycleTests
{
    [Fact]
    public async Task SwitchingTabsAndRetryingPendingConsentReuseTheSameSocketAndAttachment()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new ConcurrentDictionary<string, int>();
        var sends = new SemaphoreSlim(1, 1);
        var responses = new List<Task>();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            async Task Respond(int id, object result)
            {
                await sends.WaitAsync(deadline.Token);
                try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, result }), WebSocketMessageType.Text, true, deadline.Token); }
                finally { sends.Release(); }
            }
            try
            {
                var buffer = new byte[16384];
                while (!deadline.IsCancellationRequested)
                {
                    using var message = new MemoryStream(); WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                        if (frame.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray()); var root = json.RootElement;
                    var method = root.GetProperty("method").GetString()!; var id = root.GetProperty("id").GetInt32();
                    counts.AddOrUpdate(method, 1, (_, value) => value + 1);
                    if (method == "Target.attachToTarget")
                    {
                        var target = root.GetProperty("params").GetProperty("targetId").GetString()!;
                        if (target == "second")
                        {
                            requested.TrySetResult();
                            responses.Add(Task.Run(async () => { await consent.Task.WaitAsync(deadline.Token); await Respond(id, new { sessionId = target }); }));
                        }
                        else await Respond(id, new { sessionId = target });
                    }
                    else if (method == "Browser.getVersion") await Respond(id, new { product = "Test Chromium" });
                    else if (method == "Target.getTargets") await Respond(id, new { targetInfos = new[] {
                        new { targetId = "first", type = "page", title = "Form", url = "https://example.test/form" },
                        new { targetId = "second", type = "page", title = "Other", url = "about:blank" } } });
                    else await Respond(id, new { });
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException) { }
            finally { await Task.WhenAll(responses); }
        });
        try
        {
            await using var connection = await CdpConnection.ConnectAsync("ws://127.0.0.1:" + port + "/devtools/browser/test", deadline.Token);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var switching = connection.SwitchTabAsync("second", attempt.Token);
            await requested.Task.WaitAsync(deadline.Token); attempt.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching);
            Assert.True(connection.IsConnected); Assert.True(connection.AwaitingTabAuthorization);
            consent.TrySetResult();
            await connection.SwitchTabAsync("second", deadline.Token);
            await connection.SwitchTabAsync("first", deadline.Token);
            await connection.SwitchTabAsync("second", deadline.Token);
            Assert.Equal(2, counts["Target.attachToTarget"]);
            Assert.False(counts.ContainsKey("Target.detachFromTarget"));
            Assert.Equal(1, counts["Browser.getVersion"]);
        }
        finally { consent.TrySetResult(); deadline.Cancel(); listener.Stop(); await server; sends.Dispose(); }
    }
}
