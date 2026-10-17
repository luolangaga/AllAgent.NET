using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

internal static class EverythingSearch
{
    internal static string? Validate(string query, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024 || query.Any(char.IsControl) || query.IndexOfAny(['"', ':', '/', '\\']) >= 0)
            return "query must be filename text or a wildcard (1-1024 characters), without quotes, path separators, colons or control characters. Use path for the directory.";
        if (limit < 1) return "limit must be positive.";
        if (offset < 0 || offset > int.MaxValue - ToolOutputWindow.MaxLines - 1) return "offset is outside the supported range.";
        return null;
    }

    internal static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "everything",
        RuntimeInformation.OSArchitecture switch { Architecture.X64 => "win-x64", Architecture.X86 => "win-x86", Architecture.Arm64 => "win-arm64", _ => "unsupported" }, "es.exe");

    internal static ProcessStartInfo StartInfo(string executable, string root, string query, int limit, int offset, string instance)
    {
        // -argv matches .NET ArgumentList quoting. -search consumes exactly one
        // argument, so even a query like -exit cannot become an ES command.
        // Encode literal characters as PCRE code points so Everything operators,
        // spaces and quotes cannot change the search syntax. Only * and ? expand.
        var expression = string.Concat(query.EnumerateRunes().Select(r => r.Value == '*' ? ".*" : r.Value == '?' ? "." : $"\\x{{{r.Value:X}}}"));
        if (query.IndexOfAny(['*', '?']) >= 0) expression = "^" + expression + "$";
        var args = new List<string> { "-argv", "-instance", instance, "-timeout", "60000", "-utf8-bom", "-txt",
            "-columns", "full-path-and-name", "-no-header", "-no-footer", "-no-highlight", "-no-pause", "-no-double-quote",
            "-no-folder-append-path-separator", "-no-case", "-no-whole-word", "-no-match-path", "-sort", "path",
            "-n", (Math.Min(limit, ToolOutputWindow.MaxLines) + 1).ToString(CultureInfo.InvariantCulture),
            "-offset", offset.ToString(CultureInfo.InvariantCulture), "-path", root, "-search", "regex:" + expression };
        return BoundedProcess.StartInfo(executable, args, root);
    }

    internal static async Task<ToolResult> SearchAsync(string root, string query, int limit, int offset, CancellationToken ct,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessOutput>>? run = null)
    {
        if (run == null && !File.Exists(ExecutablePath)) return ToolResult.Failure("Bundled Everything ES client is missing or this Windows architecture is unsupported. Restore the everything/ folder from the application package.");
        try
        {
            using var engine = run == null ? await EverythingIndexer.AcquireAsync(root, ct) : null;
            var instance = engine?.Instance ?? "test";
            var info = StartInfo(ExecutablePath, root, query, limit, offset, instance);
            var output = run != null ? await run(info, ct) : await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(65), ct, limit: 1_048_576);
            ct.ThrowIfCancellationRequested();
            if (output.ExitCode != 0)
                return ToolResult.Failure(output.ExitCode == 8
                    ? "The bundled Everything indexer could not start. Retry the search; check that the application everything/ folder is intact."
                    : $"Everything search failed (ES exit code {output.ExitCode}): " + ToolResult.Clip(output.Stderr + output.Stdout, 1500));
            return CreateResult(root, query, limit, offset, output, ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        { return ToolResult.Failure("Everything search failed: " + ex.Message); }
    }

    internal static ToolResult CreateResult(string root, string query, int limit, int offset, ProcessOutput output, CancellationToken ct)
    {
        var text = output.Stdout.TrimStart('\uFEFF');
        // A capped process buffer can end mid-path; never expose the partial row.
        if (output.Truncated) text = text[..(text.LastIndexOf('\n') + 1)];
        var rows = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var pageSize = Math.Min(limit, ToolOutputWindow.MaxLines);
        var paths = new List<string>(); var consumed = 0; var bytes = 0; var outputTruncated = output.Truncated;
        foreach (var row in rows.Take(pageSize))
        {
            ct.ThrowIfCancellationRequested();
            // The index can be stale or contain junctions. Check actual host paths
            // and the approved scope independently of ES's query filtering.
            if (!IsSafeResult(root, row)) { consumed++; continue; }
            var cost = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(row, ToolResult.JsonOptions)) + 1;
            if (bytes + cost > ToolOutputWindow.MaxBytes - 2048) { outputTruncated = true; break; }
            paths.Add(row); bytes += cost; consumed++;
        }
        var hasMore = consumed < rows.Length || output.Truncated;
        if (hasMore && consumed == 0) return ToolResult.Failure("A matching path exceeds the output budget. Narrow query/path.");
        return ToolResult.Success(new { engine = "everything", query, path = root, paths, nextOffset = offset + consumed,
            hasMore, outputTruncated, notice = hasMore ? $"Continue with offset={offset + consumed}." : "Search complete. Results reflect the Everything index; unindexed files are not included." }, hasMore);
    }

    private static bool IsSafeResult(string root, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !ExternalDirectoryPermissions.Contains(root, Path.GetFullPath(path))) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
