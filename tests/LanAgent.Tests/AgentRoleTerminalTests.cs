using System.Text;
using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentRoleTerminalTests
{
    [Fact]
    public async Task RealTerminalOpensRoleEditorAndCancelsWithoutSaving()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "LanAgent-role-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var data = Path.Combine(path, "data");
            var command = "dotnet " + TerminalSession.Quote(typeof(TerminalSession).Assembly.Location) + " agents --no-initialize --workspace " + TerminalSession.Quote(path) + " --data-dir " + TerminalSession.Quote(data);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var terminal = new PseudoConsole(command, 140, 50);
            var output = new StringBuilder();
            var reader = Task.Run(() =>
            {
                var bytes = new byte[16384]; var chars = new char[16384]; var decoder = Encoding.UTF8.GetDecoder();
                try { int count; while ((count = terminal.Output.Read(bytes)) > 0) { var length = decoder.GetChars(bytes, 0, count, chars, 0); lock (output) output.Append(chars, 0, length); } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            try
            {
                await WaitText("子代理配置");
                terminal.Input.Write(Encoding.UTF8.GetBytes("\u001b[B\r"));
                await WaitText("选择角色");
                terminal.Input.Write(Encoding.UTF8.GetBytes("\r"));
                await WaitText("尚未保存");
                await WaitText("执行预算");
                terminal.Input.Write(Encoding.UTF8.GetBytes("\u001b[B\r"));
                await WaitText("角色配置未修改");
                using var store = new DataStore(data);
                Assert.Null(store.GetSetting("agents:roles"));
                Assert.True(new AgentRoleCatalog(store).Get("explore").ReadOnly);
            }
            finally { terminal.CloseSession(); await Task.WhenAny(reader, Task.Delay(2000)); }

            async Task WaitText(string expected)
            {
                while (!timeout.IsCancellationRequested)
                {
                    lock (output) if (output.ToString().Contains(expected, StringComparison.Ordinal)) return;
                    await Task.Delay(50);
                }
                lock (output) throw new Xunit.Sdk.XunitException("Missing terminal output: " + expected + "\n" + output.ToString());
            }
        }
        finally { Directory.Delete(path, true); }
    }
}
