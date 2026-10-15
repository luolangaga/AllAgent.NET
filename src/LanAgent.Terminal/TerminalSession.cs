using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace LanAgent.Terminal;

/// <summary>Launches independent UIs; explicit attach preserves legacy ConPTY compatibility.</summary>
internal static class TerminalSession
{
    private const string ModeVariable = "LANAGENT_INTERNAL_TERMINAL_MODE";
    private const byte Input = 1, Resize = 2, Output = 3, Hello = 4, Exit = 5;
    internal const int MaxPacket = 1024 * 1024;
    public static async Task<int> RunAsync(string[] raw)
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        Environment.SetEnvironmentVariable(ModeVariable, null);
        if (mode == "host") return await TerminalApp.RunAsync(raw);
        try
        {
            var args = TerminalArguments.Parse(raw);
            if (mode == "broker") return await BrokerAsync(PipeName(args.DataDirectory), raw);
            if (args.Command != "attach" || args.Flags.Contains("help")) return await TerminalApp.RunAsync(raw);
            if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("attach 需要 Windows 交互终端。");
            using var existing = new NamedPipeClientStream(".", PipeName(args.DataDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { existing.Connect(1000); }
            catch (TimeoutException) { throw new InvalidOperationException("没有可接入的旧版后台会话；运行 lanagent 开始独立聊天。"); }
            return await ClientAsync(existing);
        }
        catch (Exception ex) { Console.Error.WriteLine("LanAgent: " + ex.Message); return 1; }
    }
    internal static string PipeName(string directory) => "lanagent-session-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant())))[..32];
    private static ProcessStartInfo StartInfo(string[] raw, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 LanAgent 程序。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in raw) info.ArgumentList.Add(arg);
        info.Environment[ModeVariable] = mode;
        return info;
    }
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class Client(NamedPipeServerStream pipe) : IDisposable
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Channel<(byte Type, byte[] Data)> Outgoing { get; } = Channel.CreateBounded<(byte, byte[])>(new BoundedChannelOptions(1024) { SingleReader = true });
        public void Send(byte type, byte[] data) { if (!Outgoing.Writer.TryWrite((type, data))) Dispose(); }
        public void Dispose() { Outgoing.Writer.TryComplete(); Pipe.Dispose(); }
    }
    private static async Task<int> BrokerAsync(string name, string[] raw)
    {
        // StartInfo gives the same executable/arguments for apphost and dotnet-tool launches.
        var info = StartInfo(raw, "host");
        Environment.SetEnvironmentVariable(ModeVariable, "host");
        using var host = new PseudoConsole(string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)), 120, 30);
        Environment.SetEnvironmentVariable(ModeVariable, null);
        var gate = new object(); var connections = new HashSet<Client>();
        var startupTimer = Stopwatch.StartNew(); var hadClient = false;
        var sizes = new Dictionary<Client, (short Width, short Height)>();
        using var inputGate = new SemaphoreSlim(1, 1);
        var history = new Queue<byte[]>(); var historyBytes = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var buffer = new byte[16384];
            try
            {
                int count;
                while ((count = host.Output.Read(buffer)) > 0)
                {
                    var bytes = buffer[..count];
                    lock (gate)
                    {
                        history.Enqueue(bytes); historyBytes += bytes.Length;
                        while (historyBytes > 4 * MaxPacket || history.Count > 512) historyBytes -= history.Dequeue().Length;
                        foreach (var client in connections) client.Send(Output, bytes);
                    }
                }
            }
            catch (IOException) { }
        });
        var monitor = Task.Run(async () =>
        {
            var stopping = new Stopwatch();
            while (!host.HasExited)
            {
                bool emptyStartup;
                lock (gate) emptyStartup = !hadClient && startupTimer.Elapsed > TimeSpan.FromSeconds(30);
                if (emptyStartup) { stop.Cancel(); host.CloseSession(); }
                if (stop.IsCancellationRequested)
                {
                    if (!stopping.IsRunning) stopping.Start();
                    if (stopping.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        using var process = Process.GetProcessById(host.ProcessId);
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                }
                await Task.Delay(100);
            }
            host.CloseSession();
            // ConPTY output is drained before reporting completion.
            await Task.WhenAny(reader, Task.Delay(1000));
            lock (gate) { foreach (var client in connections) { client.Send(Exit, BitConverter.GetBytes(host.ExitCode)); client.Outgoing.Writer.TryComplete(); } }
            stop.Cancel();
        });
        var clients = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); throw; }
                var client = new Client(pipe);
                lock (gate)
                {
                    if (stop.IsCancellationRequested) { client.Dispose(); break; }
                    hadClient = true;
                    connections.Add(client);
                    client.Send(Hello, BitConverter.GetBytes(host.ProcessId));
                    foreach (var bytes in history) client.Send(Output, bytes);
                }
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel(); host.CloseSession();
            await monitor;
            await Task.WhenAny(Task.WhenAll(clients), Task.Delay(1000));
            lock (gate) foreach (var client in connections) client.Dispose();
        }
        return host.ExitCode;

        async Task ServeAsync(Client client)
        {
            var send = Task.Run(async () =>
            {
                await foreach (var packet in client.Outgoing.Reader.ReadAllAsync()) await WritePacketAsync(client.Pipe, packet.Type, packet.Data);
            });
            var receive = Task.Run(async () =>
            {
                var firstResize = true;
                while (await ReadPacketAsync(client.Pipe) is { } packet)
                {
                    await inputGate.WaitAsync();
                    try
                    {
                        lock (gate) { if (!connections.Contains(client) || host.HasExited) return; }
                        if (packet.Type == Input && packet.Data.Length <= 4096) host.Input.Write(packet.Data);
                        else if (packet.Type == Resize && packet.Data.Length == 4)
                        {
                            var width = BitConverter.ToInt16(packet.Data, 0); var height = BitConverter.ToInt16(packet.Data, 2);
                            if (width is < 1 or > 500 || height is < 1 or > 200) throw new IOException("Invalid terminal size.");
                            // One shared screen must fit every connected terminal.
                            lock (gate)
                            {
                                sizes[client] = (width, height);
                                width = sizes.Values.Min(size => size.Width); height = sizes.Values.Min(size => size.Height);
                            }
                            // Force ConPTY to repaint even when reattaching at the same dimensions.
                            if (firstResize) { host.Resize((short)(width == 500 ? 499 : width + 1), height); firstResize = false; }
                            host.Resize(width, height);
                        }
                        else throw new IOException("Invalid terminal packet.");
                    }
                    finally { inputGate.Release(); }
                }
            });
            try { await await Task.WhenAny(send, receive); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally
            {
                var lastClient = false;
                lock (gate)
                {
                    connections.Remove(client); sizes.Remove(client);
                    if (hadClient && connections.Count == 0) { lastClient = true; stop.Cancel(); }
                }
                if (lastClient) host.CloseSession();
                client.Dispose();
                try { await Task.WhenAll(send, receive); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
    }
    private static async Task<int> ClientAsync(NamedPipeClientStream pipe)
    {
        var first = await ReadPacketAsync(pipe);
        if (first is not { Type: Hello, Data.Length: 4 }) throw new IOException("后台会话协议不匹配。");
        using var modes = new ClientConsoleModes();
        Console.WriteLine($"已接入 LanAgent 进程 {BitConverter.ToInt32(first.Value.Data)}；沿用该会话的工作区和权限。关闭终端或 Ctrl+] 断开；最后一个终端断开后后台自动退出。");
        using var done = new CancellationTokenSource();
        var outgoing = Channel.CreateUnbounded<(byte Type, byte[] Data)>();
        using var output = Console.OpenStandardOutput();
        var keys = Task.Run(() =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    var bytes = ClientKeyboard.Read(out var detach);
                    if (detach) { done.Cancel(); break; }
                    if (bytes.Length > 0) outgoing.Writer.TryWrite((Input, bytes));
                }
            }
            catch (IOException) { done.Cancel(); }
        });
        var sizing = Task.Run(async () =>
        {
            (int, int) previous = default;
            while (!done.IsCancellationRequested)
            {
                var size = (Math.Clamp(Console.WindowWidth, 1, 500), Math.Clamp(Console.WindowHeight, 1, 200));
                if (size != previous)
                {
                    outgoing.Writer.TryWrite((Resize, [.. BitConverter.GetBytes((short)size.Item1), .. BitConverter.GetBytes((short)size.Item2)])); previous = size;
                }
                await Task.Delay(100, done.Token);
            }
        });
        var send = Task.Run(async () =>
        {
            await foreach (var packet in outgoing.Reader.ReadAllAsync(done.Token)) await WritePacketAsync(pipe, packet.Type, packet.Data, done.Token);
        });
        var code = 0;
        try
        {
            while (await ReadPacketAsync(pipe, done.Token) is { } packet)
            {
                if (packet.Type == Output) { await output.WriteAsync(packet.Data, done.Token); await output.FlushAsync(done.Token); }
                else if (packet.Type == Exit && packet.Data.Length == 4) { code = BitConverter.ToInt32(packet.Data); break; }
                else throw new IOException("后台会话返回了无效数据。");
            }
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested) { }
        finally
        {
            done.Cancel(); outgoing.Writer.TryComplete(); pipe.Dispose();
            try { await Task.WhenAll(send, sizing); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            // Don't wait on a blocking keyboard read: this lightweight client exits now.
            await output.WriteAsync(Encoding.UTF8.GetBytes("\u001b[?9001l\u001b[?1004l\u001b[?1000l\u001b[?1002l\u001b[?1003l\u001b[?1006l\u001b[?2004l\u001b[?1049l\u001b[?25h\u001b[0m"));
            await output.FlushAsync();
        }
        Console.WriteLine("已离开终端连接；最后一个连接断开后后台自动退出。");
        return code;
    }
    internal static async Task WritePacketAsync(Stream stream, byte type, byte[] data, CancellationToken ct = default)
    {
        if (data.Length > MaxPacket) throw new IOException("Terminal packet too large.");
        await stream.WriteAsync(new byte[] { type }.Concat(BitConverter.GetBytes(data.Length)).ToArray(), ct);
        await stream.WriteAsync(data, ct); await stream.FlushAsync(ct);
    }
    internal static async Task<(byte Type, byte[] Data)?> ReadPacketAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        if (await stream.ReadAsync(header.AsMemory(0, 1), ct) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = BitConverter.ToInt32(header, 1);
        if (length < 0 || length > MaxPacket) throw new IOException("Invalid terminal packet length.");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, ct);
        return (header[0], data);
    }
    private sealed class ClientConsoleModes : IDisposable
    {
        private readonly IntPtr _input = GetStdHandle(-10), _output = GetStdHandle(-11);
        private readonly uint _inputMode, _outputMode;
        private readonly Encoding _inputEncoding = Console.InputEncoding, _outputEncoding = Console.OutputEncoding;
        public ClientConsoleModes()
        {
            if (!GetConsoleMode(_input, out _inputMode) || !GetConsoleMode(_output, out _outputMode)) throw new IOException("无法读取终端模式。");
            if (!SetConsoleMode(_input, (_inputMode | 0x0010 | 0x0080) & ~(0x0001u | 0x0002u | 0x0004u | 0x0040u | 0x0200u)) || !SetConsoleMode(_output, _outputMode | 0x0004))
            { Dispose(); throw new IOException("无法设置终端模式。"); }
            Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        }
        public void Dispose()
        {
            Console.InputEncoding = _inputEncoding; Console.OutputEncoding = _outputEncoding;
            SetConsoleMode(_input, _inputMode); SetConsoleMode(_output, _outputMode);
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    }
}
