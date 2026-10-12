using System.Diagnostics;
using System.Text;

namespace LanAgent.Core.Runtime;

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool Truncated);

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

    public static async Task<ProcessOutput> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct, int limit = LanAgent.Core.Tools.ToolResult.MaxOutputCharacters, Action<string, bool>? onOutput = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = info };
        process.Start();
        if (info.RedirectStandardInput) process.StandardInput.Close();
        var stdout = DrainAsync(process.StandardOutput, limit, linked.Token, text => onOutput?.Invoke(text, false));
        var stderr = DrainAsync(process.StandardError, limit, linked.Token, text => onOutput?.Invoke(text, true));
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

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken ct, Action<string>? onOutput = null)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var remaining = limit - result.Length;
            result.Append(buffer, 0, Math.Min(count, remaining));
            if (remaining > 0) onOutput?.Invoke(new string(buffer, 0, Math.Min(count, remaining)));
            truncated |= count > remaining;
        }
        return (result.ToString(), truncated);
    }
}
