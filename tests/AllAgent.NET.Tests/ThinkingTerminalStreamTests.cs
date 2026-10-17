using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Terminal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Hyperlight acceptance")]
public sealed class ThinkingTerminalStreamTests
{
    [Fact]
    public async Task ThinkingUpdatesInRealTerminalBeforeAndAfterToolExecution()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            var first = Interlocked.Increment(ref requests) == 1;
            context.Response.ContentType = "text/event-stream";
            async Task Send(object delta, string? finish = null)
            {
                await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n");
                await context.Response.Body.FlushAsync();
            }
            await Send(new { reasoning_content = first ? "THINK-BEFORE-TOOL" : "THINK-AFTER-TOOL" });
            await (first ? firstRelease : finalRelease).Task.WaitAsync(context.RequestAborted);
            if (first) await Send(new { tool_calls = new[] { new { index = 0, id = "read", type = "function", function = new { name = "execute_code", arguments = "{\"code\":\"print(call_tool('file_read', path='proof.txt'))\"}" } } } }, "tool_calls");
            else await Send(new { content = "ANSWER-PROOF" }, "stop");
            await context.Response.WriteAsync("data: [DONE]\n\n");
        });
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "proof.txt"), "fixture");
        var directory = Path.Combine(workspace.Root, "data");
        using (var store = new DataStore(directory))
        {
            var config = new ApiConfig { ProviderId = "openai", BaseUrl = server.Url, ApiKey = "fixture", DefaultModel = "thinking-fixture" };
            store.AddApiConfig(config); store.SetSetting("terminal:lastConfig", config.Id.ToString());
        }
        var command = "dotnet " + TerminalSession.Quote(typeof(TerminalSession).Assembly.Location)
            + " --no-initialize --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(directory)
            + " --python " + TerminalSession.Quote(workspace.Python) + " --pwsh " + TerminalSession.Quote(workspace.Pwsh);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var terminal = new PseudoConsole(command, 100, 30);
        var output = new StringBuilder();
        var reader = Task.Run(() =>
        {
            using var text = new StreamReader(terminal.Output, Encoding.UTF8, leaveOpen: true);
            var buffer = new char[8192];
            try { int count; while ((count = text.Read(buffer, 0, buffer.Length)) > 0) lock (output) output.Append(buffer, 0, count); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        try
        {
            await Wait("thinking-fixture");
            terminal.Input.Write(Encoding.UTF8.GetBytes("Read proof.txt\r"));
            await Wait("THINK-BEFORE-TOOL"); firstRelease.TrySetResult();
            await Wait("THINK-AFTER-TOOL"); finalRelease.TrySetResult();
            await Wait("ANSWER-PROOF");
        }
        finally
        {
            firstRelease.TrySetResult(); finalRelease.TrySetResult(); terminal.CloseSession();
            await Task.WhenAny(reader, Task.Delay(2000));
        }
        async Task Wait(string expected)
        {
            try { while (true) { lock (output) if (output.ToString().Contains(expected)) return; await Task.Delay(25, timeout.Token); } }
            catch (OperationCanceledException) { lock (output) throw new Xunit.Sdk.XunitException("Missing " + expected + "\n" + output.ToString()[Math.Max(0, output.Length - 8000)..]); }
        }
    }
}
