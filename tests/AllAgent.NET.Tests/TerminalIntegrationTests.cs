using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class TerminalIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedJsonRunInheritsPermissionModeWithoutWaitingForUnavailableApproval(bool fullAccess)
    {
        using var workspace = new TestWorkspace();
        var requests = new ConcurrentQueue<JsonElement>();
        var parentCalls = 0; var childCalls = 0;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var request = document.RootElement.Clone(); requests.Enqueue(request);
            var child = request.GetProperty("model").GetString() == "child-test-model";
            var first = (child ? Interlocked.Increment(ref childCalls) : Interlocked.Increment(ref parentCalls)) == 1;
            object delta = first
                ? new { tool_calls = new[] { new { index = 0, id = child ? "child-write" : "delegate", type = "function", function = new { name = "execute_code", arguments = JsonSerializer.Serialize(new { code = child
                    ? "r = call_tool('file_write', path='child-proof.txt', content='delegated proof')\nprint(r)"
                    : "r = call_tool('subagent', agent='modify', task='Write child-proof.txt')\nprint(r)" }) } } } }
                : new { content = child ? "Child finished" : "Parent finished" };
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = first ? "tool_calls" : "stop" } } }) + "\n\ndata: [DONE]\n\n");
        });
        var exe = Path.Combine(AppContext.BaseDirectory, "allagent.exe");
        var arguments = new List<string> { "run", "Delegate the write", "--json", "--subagent-model", "child-test-model", "--workspace", workspace.Root, "--data-dir", Path.Combine(workspace.Root, "sessions"), "--python", workspace.Python, "--pwsh", workspace.Pwsh };
        arguments.Add(fullAccess ? "--full-access" : "--ask");
        var info = BoundedProcess.StartInfo(exe, arguments);
        info.Environment["ALLAGENT_MODEL"] = "parent-test-model";
        info.Environment["ALLAGENT_API_KEY"] = "local-test-key";
        info.Environment["ALLAGENT_BASE_URL"] = server.Url;
        info.Environment["ALLAGENT_PROVIDER"] = "openai";
        var output = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(90), default, 64000);
        Assert.True(output.ExitCode == 0, output.Stdout + output.Stderr);
        Assert.Contains("Parent finished", output.Stdout);
        var childRequest = Assert.Single(requests, request => request.GetProperty("model").GetString() == "child-test-model" && request.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool"));
        var result = Assert.Single(childRequest.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "tool").GetProperty("content").GetString();
        if (fullAccess) Assert.Equal("delegated proof", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "child-proof.txt")));
        else { Assert.False(File.Exists(Path.Combine(workspace.Root, "child-proof.txt"))); Assert.Contains("approval denied", result, StringComparison.OrdinalIgnoreCase); }
    }

    [Fact]
    public async Task TerminalRunsSplitSseCodeActToolsAndPersistsSession()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "proof.txt"), "local proof");
        await using var server = new MockChatServer();
        var exe = Path.Combine(AppContext.BaseDirectory, "allagent.exe");
        var dataDirectory = Path.Combine(workspace.Root, "sessions");
        var info = BoundedProcess.StartInfo(exe, ["run", "Read proof.txt", "--json", "--workspace", workspace.Root, "--data-dir", dataDirectory, "--python", workspace.Python, "--pwsh", workspace.Pwsh]);
        info.Environment["ALLAGENT_MODEL"] = "local-test-model";
        info.Environment["ALLAGENT_API_KEY"] = "local-test-key";
        info.Environment["ALLAGENT_BASE_URL"] = server.Url;
        info.Environment["ALLAGENT_PROVIDER"] = "openai";
        var output = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(90), default, 64000);
        Assert.Equal(0, output.ExitCode);
        Assert.Empty(output.Stderr);
        var records = output.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var events = records.Where(record => record.TryGetProperty("type", out _)).ToArray();
        Assert.Equal("AgentDone", events.Last().GetProperty("type").GetString());
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "TokenDelta" && evt.GetProperty("data").GetProperty("text").GetString() == "Verified local proof");
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "ToolCallCompleted" && evt.GetProperty("data").GetProperty("toolName").GetString() == "file_read");
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.Equal("execute_code", Assert.Single(request.GetProperty("tools").EnumerateArray()).GetProperty("function").GetProperty("name").GetString()));
        var second = server.Requests.Last();
        var toolMessage = Assert.Single(second.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "tool");
        Assert.Contains("local proof", toolMessage.GetProperty("content").GetString());
        Assert.Equal("split-call", toolMessage.GetProperty("tool_call_id").GetString());
        Assert.True(File.Exists(Path.Combine(dataDirectory, "allagent.json")));
    }

    private sealed class MockChatServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public string Url { get; }
        public MockChatServer()
        {
            _listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(_stop.Token);
                    Assert.StartsWith("POST /chat/completions", requestLine);
                    var length = 0;
                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token)))
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
                    var body = new char[length];
                    var position = 0;
                    while (position < length) position += await reader.ReadAsync(body.AsMemory(position), _stop.Token);
                    Requests.Enqueue(JsonSerializer.Deserialize<JsonElement>(new string(body)));
                    var sse = new StringBuilder();
                    void Delta(object delta, string? finish = null) => sse.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } })).Append("\n\n");
                    if (Requests.Count == 1)
                    {
                        var args = JsonSerializer.Serialize(new { code = "r = call_tool('file_read', path='proof.txt')\nprint(r['data']['content'])" });
                        var middle = args.Length / 2;
                        Delta(new { tool_calls = new[] { new { index = 0, id = "split-call", type = "function", function = new { name = "execute_code", arguments = args[..middle] } } } });
                        Delta(new { tool_calls = new[] { new { index = 0, function = new { arguments = args[middle..] } } } });
                        Delta(new { }, "tool_calls");
                    }
                    else { Delta(new { content = "Verified local proof" }); Delta(new { }, "stop"); }
                    sse.Append("data: [DONE]\n\n");
                    var bytes = Encoding.UTF8.GetBytes(sse.ToString());
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; _stop.Dispose(); }
    }
}
