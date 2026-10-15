using System.Diagnostics;
using System.Text;

namespace LanAgent.Core.Runtime;

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool Truncated)
{
    public bool TimedOut { get; init; }
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
}

public static class BoundedProcess
{
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string? directory = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = directory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<ProcessOutput> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct, int limit = LanAgent.Core.Tools.ToolResult.MaxOutputCharacters, Action<string, bool>? onOutput = null, bool stripAnsi = false, string? tailOutputDirectory = null)
    {
        if (tailOutputDirectory != null) return await RunWithTailAsync(info, timeout, ct, onOutput, stripAnsi, tailOutputDirectory);
        using var linked = new ProcessDeadline(ct, timeout);
        using var process = new Process { StartInfo = info };
        process.Start();
        if (info.RedirectStandardInput) process.StandardInput.Close();
        var stdout = DrainAsync(process.StandardOutput, limit, linked.Token, text => onOutput?.Invoke(text, false), stripAnsi);
        var stderr = DrainAsync(process.StandardError, limit, linked.Token, text => onOutput?.Invoke(text, true), stripAnsi);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Process exceeded {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task<ProcessOutput> RunWithTailAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct,
        Action<string, bool>? onOutput, bool stripAnsi, string directory)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        using var output = new ProcessOutputAccumulator(Path.Combine(directory, id + ".stdout.log"));
        using var error = new ProcessOutputAccumulator(Path.Combine(directory, id + ".stderr.log"));
        using var linked = new ProcessDeadline(ct, timeout);
        using var process = new Process { StartInfo = info }; process.Start();
        if (info.RedirectStandardInput) process.StandardInput.Close();
        async Task Drain(StreamReader reader, ProcessOutputAccumulator accumulator, bool stderr)
        {
            var buffer = new char[2048]; var ansi = new AnsiFilter(); int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                var text = stripAnsi ? ansi.Clean(buffer.AsSpan(0, count)) : new string(buffer, 0, count);
                accumulator.Append(text); onOutput?.Invoke(text, stderr);
            }
        }
        var stdout = Drain(process.StandardOutput, output, false); var stderr = Drain(process.StandardError, error, true);
        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) { Kill(process); timedOut = !ct.IsCancellationRequested; }
        finally
        {
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                // Detached descendants can retain pipe handles. Close the readers and
                // join both drains before disposing their accumulators.
                process.StandardOutput.Dispose(); process.StandardError.Dispose();
                try { await Task.WhenAll(stdout, stderr); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            output.Flush(); error.Flush();
        }
        ct.ThrowIfCancellationRequested();
        return new(timedOut ? -1 : process.ExitCode, output.Snapshot(), error.Snapshot(), output.Truncated || error.Truncated)
        { TimedOut = timedOut, StdoutPath = output.FullOutputPath, StderrPath = error.FullOutputPath };
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken ct, Action<string>? onOutput = null, bool stripAnsi = false)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        var truncated = false;
        var ansi = new AnsiFilter();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var text = stripAnsi ? ansi.Clean(buffer.AsSpan(0, count)) : new string(buffer, 0, count);
            var remaining = limit - result.Length;
            result.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
            if (remaining > 0 && text.Length > 0) onOutput?.Invoke(text[..Math.Min(text.Length, remaining)]);
            truncated |= text.Length > remaining;
        }
        return (result.ToString(), truncated);
    }

    // Retain parser state across stream chunks, including split CSI and OSC sequences.
    private sealed class AnsiFilter
    {
        private int _state;
        public string Clean(ReadOnlySpan<char> text)
        {
            var clean = new StringBuilder();
            foreach (var c in text)
            {
                switch (_state)
                {
                    case 0: if (c == '\u001b') _state = 1; else if (c == '\u009b') _state = 2; else clean.Append(c); break;
                    case 1: _state = c == '[' ? 2 : c is ']' or 'P' or '^' or '_' ? 3 : c is >= ' ' and <= '/' ? 5 : 0; break;
                    case 2: if (c is >= '@' and <= '~') _state = 0; break;
                    case 3: if (c == '\a') _state = 0; else if (c == '\u001b') _state = 4; break;
                    case 4: _state = c == '\\' ? 0 : c == '\u001b' ? 4 : 3; break;
                    case 5: if (c is >= '0' and <= '~') _state = 0; break;
                }
            }
            return clean.ToString();
        }
    }
}
