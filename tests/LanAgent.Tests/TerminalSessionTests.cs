using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class TerminalSessionTests
{
    [Fact]
    public void ExitCommandsAreAbsentFromCatalogAndKeyboard()
    {
        Assert.DoesNotContain(CommandCatalog.Builtins, command => command.Text is "/exit" or "/quit");
        var input = new InputState();
        Assert.Null(input.Key(new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true), [], []));
        Assert.Empty(input.Text);
    }

    [Fact]
    public async Task PacketRoundTripAndInvalidLengths()
    {
        using var stream = new MemoryStream();
        await TerminalSession.WritePacketAsync(stream, 3, Encoding.UTF8.GetBytes("你好"));
        stream.Position = 0;
        var packet = await TerminalSession.ReadPacketAsync(stream);
        Assert.Equal((byte)3, packet!.Value.Type); Assert.Equal("你好", Encoding.UTF8.GetString(packet.Value.Data));
        Assert.Null(await TerminalSession.ReadPacketAsync(stream));
        using var invalid = new MemoryStream([1, .. BitConverter.GetBytes(-1)]);
        await Assert.ThrowsAsync<IOException>(() => TerminalSession.ReadPacketAsync(invalid));
    }
    [Theory]
    [InlineData("hello", "\"hello\"")]
    [InlineData("C:\\my folder\\", "\"C:\\my folder\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void WindowsCommandLineQuoting(string input, string expected) => Assert.Equal(expected, TerminalSession.Quote(input));

    [Fact]
    public async Task ActualLaunchersKeepIndependentInputSessionsAndClose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        using (var store = new LanAgent.Core.Data.DataStore(directory))
        {
            var model = new LanAgent.Core.Models.ApiConfig { DisplayName = "REMEMBERED-PROVIDER", DefaultModel = "remembered-model", Models = ["remembered-model"], IsEnabled = true };
            store.AddApiConfig(model); store.SetSetting("terminal:lastConfig", model.Id.ToString());
        }
        var command = "dotnet " + TerminalSession.Quote(typeof(TerminalSession).Assembly.Location)
            + " --no-initialize --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var terminals = new List<PseudoConsole>(); var readers = new List<Task>();
        try
        {
            var first = Start(); await WaitText(first.Text, "remembered-model");
            var second = Start(); await WaitText(second.Text, "remembered-model");
            Assert.NotEqual(first.Console.ProcessId, second.Console.ProcessId);
            using (var snapshot = new LanAgent.Core.Data.DataStore(directory)) Assert.Equal(2, snapshot.Conversations.Count);
            second.Console.Input.Write(Encoding.UTF8.GetBytes("SECOND-INPUT"));
            await WaitText(second.Text, "SECOND-INPUT");
            first.Console.Input.Write(Encoding.UTF8.GetBytes("FIRST-INPUT"));
            await WaitText(first.Text, "FIRST-INPUT");
            second.Console.Resize(80, 25);
            await Task.Delay(300, timeout.Token);
            lock (first.Text) Assert.DoesNotContain("SECOND-INPUT", first.Text.ToString());
            lock (second.Text) Assert.DoesNotContain("FIRST-INPUT", second.Text.ToString());
            second.Console.CloseSession();
            while (!second.Console.HasExited) await Task.Delay(50, timeout.Token);
            Assert.False(first.Console.HasExited);
            first.Console.Input.Write(Encoding.UTF8.GetBytes(new string('\b', 11) + "/new\r"));
            while (true)
            {
                using var snapshot = new LanAgent.Core.Data.DataStore(directory);
                if (snapshot.Conversations.Count == 3) break;
                await Task.Delay(50, timeout.Token);
            }
            first.Console.CloseSession();
            while (!first.Console.HasExited) await Task.Delay(50, timeout.Token);
            using var reloaded = new LanAgent.Core.Data.DataStore(directory);
            Assert.Equal(3, reloaded.Conversations.Count);
        }
        finally
        {
            foreach (var terminal in terminals) terminal.Dispose();
            await Task.WhenAny(Task.WhenAll(readers), Task.Delay(2000));
        }
        (PseudoConsole Console, StringBuilder Text) Start()
        {
            var terminal = new PseudoConsole(command, 120, 40); terminals.Add(terminal);
            var text = new StringBuilder();
            readers.Add(Task.Run(() =>
            {
                var bytes = new byte[16384]; var decoder = Encoding.UTF8.GetDecoder(); var chars = new char[16384]; int count;
                try { while ((count = terminal.Output.Read(bytes)) > 0) { var length = decoder.GetChars(bytes, 0, count, chars, 0); lock (text) text.Append(chars, 0, length); } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }));
            readers.Add(Task.Run(async () => { while (!terminal.HasExited) await Task.Delay(50); terminal.CloseSession(); }));
            return (terminal, text);
        }
        async Task WaitText(StringBuilder text, string expected)
        {
            while (true)
            {
                lock (text) { if (text.ToString().Contains(expected)) return; }
                await Task.Delay(50, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task RealPseudoConsoleRunsInteractiveProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var console = new PseudoConsole("cmd.exe /d /q /c echo LANAGENT-CONPTY-OK", 80, 25);
        var captured = new StringBuilder();
        var read = Task.Run(() =>
        {
            var buffer = new byte[4096]; var text = captured;
            int count;
            while ((count = console.Output.Read(buffer)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                if (text.ToString().Contains("LANAGENT-CONPTY-OK")) return text.ToString();
            }
            return text.ToString();
        });
        var close = Task.Run(async () => { while (!console.HasExited) await Task.Delay(50); console.CloseSession(); });
        try { Assert.Contains("LANAGENT-CONPTY-OK", await read.WaitAsync(TimeSpan.FromSeconds(5))); await close; }
        catch { throw new Exception("ConPTY output=" + captured.ToString().Replace("\u001b", "<ESC>") + "; exited=" + console.HasExited); }
    }

    [Fact]
    public async Task SharedHostExitsOnlyAfterLastTerminalDisconnects()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "lanagent-attach-" + Guid.NewGuid());
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(TerminalSession).Assembly.Location);
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(directory);
        info.ArgumentList.Add("--no-initialize");
        info.Environment["LANAGENT_INTERNAL_TERMINAL_MODE"] = "broker";
        using var broker = Process.Start(info)!;
        int? hostPid = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var first = NewClient(); await first.ConnectAsync(timeout.Token);
            var hello = await TerminalSession.ReadPacketAsync(first, timeout.Token);
            Assert.Equal((byte)4, hello!.Value.Type); hostPid = BitConverter.ToInt32(hello.Value.Data);
            using var host = Process.GetProcessById(hostPid.Value);
            Assert.False(host.HasExited);
            using var second = NewClient(); await second.ConnectAsync(timeout.Token);
            hello = await TerminalSession.ReadPacketAsync(second, timeout.Token);
            Assert.Equal(hostPid, BitConverter.ToInt32(hello!.Value.Data));
            (byte Type, byte[] Data)? packet;
            second.Dispose();
            await Task.Delay(200, timeout.Token);
            Assert.False(host.HasExited);
            using var third = NewClient(); await third.ConnectAsync(timeout.Token);
            hello = await TerminalSession.ReadPacketAsync(third, timeout.Token);
            Assert.Equal(hostPid, BitConverter.ToInt32(hello!.Value.Data));
            await TerminalSession.WritePacketAsync(third, 2, [.. BitConverter.GetBytes((short)90), .. BitConverter.GetBytes((short)30)], timeout.Token);
            // Wait for the actual fullscreen prompt before closing the clients.
            var screen = new StringBuilder();
            while (!screen.ToString().Contains("LanAgent"))
            {
                packet = await TerminalSession.ReadPacketAsync(third, timeout.Token);
                Assert.NotNull(packet); Assert.Equal((byte)3, packet.Value.Type);
                screen.Append(Encoding.UTF8.GetString(packet.Value.Data));
            }
            first.Dispose();
            await Task.Delay(200, timeout.Token);
            Assert.False(host.HasExited);
            third.Dispose();
            await host.WaitForExitAsync(timeout.Token);
            await broker.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!broker.HasExited) { broker.Kill(entireProcessTree: true); await broker.WaitForExitAsync(); }
            if (hostPid is { } pid)
            {
                try { using var host = Process.GetProcessById(pid); if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (ArgumentException) { }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        NamedPipeClientStream NewClient() => new(".", TerminalSession.PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}
