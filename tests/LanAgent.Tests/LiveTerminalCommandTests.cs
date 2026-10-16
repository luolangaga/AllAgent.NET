using System.Text;
using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class LiveTerminalCommandTests
{
    [Fact]
    public async Task RealTerminalAcceptsCommandsDuringDelegationAndUpdatesAfterChildCompletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var childRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentCalls = 0; var childCalls = 0;
        string? returnedChildResult = null;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            var child = request.RootElement.GetProperty("model").GetString() == "child-test-model";
            var first = (child ? Interlocked.Increment(ref childCalls) : Interlocked.Increment(ref parentCalls)) == 1;
            if (!first)
            {
                if (child) { childWaiting.TrySetResult(); await childRelease.Task.WaitAsync(context.RequestAborted); }
                else
                {
                    returnedChildResult = string.Join('\n', request.RootElement.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "tool").Select(message => message.GetProperty("content").GetString()));
                    parentWaiting.TrySetResult(); await parentRelease.Task.WaitAsync(context.RequestAborted);
                }
            }
            object delta = first
                ? new { tool_calls = new[] { new { index = 0, id = child ? "read" : "delegate", type = "function", function = new { name = "execute_code", arguments = JsonSerializer.Serialize(new { code = child
                    ? "print(call_tool('file_read', path='proof.txt'))"
                    : "print(call_tool('subagent', agent='general', task='Read proof.txt'))" }) } } } }
                : new { content = child ? "CHILD-FINAL-PROOF" : "PARENT-FINAL-PROOF" };
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = first ? "tool_calls" : "stop" } } }) + "\n\ndata: [DONE]\n\n");
        });
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "proof.txt"), "LATEST-STEP-PROOF");
        var directory = Path.Combine(workspace.Root, "data");
        using (var store = new DataStore(directory))
        {
            var config = new ApiConfig { ProviderId = "openai", DisplayName = "PTY-TEST", BaseUrl = server.Url, ApiKey = "local-key", DefaultModel = "parent-test-model", Models = ["parent-test-model"] };
            store.AddApiConfig(config); store.SetSetting("terminal:lastConfig", config.Id.ToString());
        }
        var command = "dotnet " + TerminalSession.Quote(typeof(TerminalSession).Assembly.Location)
            + " --no-initialize --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(directory)
            + " --subagent-model child-test-model --python " + TerminalSession.Quote(workspace.Python) + " --pwsh " + TerminalSession.Quote(workspace.Pwsh);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var terminal = new PseudoConsole(command, 140, 50);
        var text = new StringBuilder();
        var reader = Task.Run(() =>
        {
            var bytes = new byte[16384]; var chars = new char[16384]; var decoder = Encoding.UTF8.GetDecoder();
            try { int count; while ((count = terminal.Output.Read(bytes)) > 0) { var length = decoder.GetChars(bytes, 0, count, chars, 0); lock (text) text.Append(chars, 0, length); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        try
        {
            await WaitText("parent-test-model");
            terminal.Input.Write(Encoding.UTF8.GetBytes("Read the test file\r"));
            await childWaiting.Task.WaitAsync(timeout.Token);
            await WaitText("LATEST-STEP-PROOF");
            terminal.Input.Write(Encoding.UTF8.GetBytes("/status\r")); await WaitText("执行中 · 会话状态");
            terminal.Input.Write(Encoding.UTF8.GetBytes("/tasks\r")); await WaitText("后台任务 / 子 Agent");
            await using var snapshot = new BackgroundTasks(directory, readOnly: true);
            var job = Assert.Single(snapshot.List);
            terminal.Input.Write(Encoding.UTF8.GetBytes("/tasks show " + job.Id + "\r")); await WaitText("Read proof.txt");
            childRelease.TrySetResult(); await parentWaiting.Task.WaitAsync(timeout.Token);
            Assert.Contains("CHILD-FINAL-PROOF", returnedChildResult);
            await WaitText("CHILD-FINAL-PROOF"); await WaitText("处理子代理结果");
            parentRelease.TrySetResult(); await WaitText("PARENT-FINAL-PROOF");
            lock (text) Assert.DoesNotContain("其他命令请在任务结束后使用", text.ToString());
        }
        finally
        {
            childRelease.TrySetResult(); parentRelease.TrySetResult(); terminal.CloseSession();
            await Task.WhenAny(reader, Task.Delay(2000));
        }
        async Task WaitText(string expected)
        {
            try
            {
                while (true) { lock (text) if (text.ToString().Contains(expected)) return; await Task.Delay(50, timeout.Token); }
            }
            catch (OperationCanceledException) { lock (text) throw new Xunit.Sdk.XunitException("Missing: " + expected + "\n" + text.ToString()[Math.Max(0, text.Length - 10000)..]); }
        }
    }
}
