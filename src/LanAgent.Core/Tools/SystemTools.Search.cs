using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.Tools;

public partial class SystemTools
{
    private static readonly string[] SearchIgnored = [".git", "bin", "obj", "node_modules", ".venv", "__pycache__", ".mimosa", "dist", ".lanagent"];

    [AgentTool("file_glob"), Description("Find file paths with ripgrep's ignore-aware file listing. Respects .gitignore, skips symlinks and build/cache directories by default. Glob '*' matches one segment and '**' recurses. Sorted, bounded results include nextOffset. Requires installed rg.")]
    public async Task<ToolResult> GlobFilesAsync([Description("Nonempty glob such as *.ts, **/*.cs or **/Strings/*.ts")] string pattern,
        [Description("Directory path; '.' is workspace root")] string path = ".",
        [Description("Positive requested path count; results are paginated by output budget")] int limit = 50,
        [Description("Nonnegative number of matching paths to skip")] int offset = 0,
        [Description("Bypass ignore rules and include build/cache directories")] bool include_ignored = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(pattern)) return ToolResult.Failure("pattern must be nonempty.");
        if (limit < 1) return ToolResult.Failure("limit must be positive.");
        if (offset < 0) return ToolResult.Failure("offset must be nonnegative.");
        var pageLimit = Math.Min(limit, ToolOutputWindow.MaxLines);
        var root = workspace.Resolve(path);
        if (!Directory.Exists(root)) return ToolResult.Failure("Glob path must be an existing directory.");
        // Keep the existing segment-based glob contract while rg supplies discovery
        // and Git ignore semantics. rg -g '*.ts' alone matches any depth.
        var matcher = GlobExpression(pattern);
        var paths = new List<string>(); var found = 0; var outputBytes = 0;
        var scanTruncated = false; var outputTruncated = false; var hasMore = false;
        var args = RipgrepArgs(include_ignored); args.AddRange(["--files", "--null", "--", root]);
        var run = await RunRipgrepAsync(args, value =>
        {
            if (!SafeDiscoveredPath(value, root) || !matcher.IsMatch(Path.GetRelativePath(root, value).Replace('\\', '/'))) return true;
            found++;
            if (found <= offset) return true;
            if (paths.Count == pageLimit) { hasMore = true; return false; }
            var relative = Path.GetRelativePath(workspace.Root, value).Replace('\\', '/');
            var cost = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(relative, ToolResult.JsonOptions));
            if (outputBytes + cost > ToolOutputWindow.MaxBytes) { outputTruncated = true; hasMore = true; return false; }
            paths.Add(relative); outputBytes += cost;
            return true;
        }, ct, '\0');
        if (run.Error != null) return ToolResult.Failure(run.Error);
        if (outputTruncated && paths.Count == 0) return ToolResult.Failure("A matching path exceeds the output budget; use a narrower search directory so paths are shorter.");
        return ToolResult.Success(new { pattern, path, paths, nextOffset = offset + paths.Count, scanTruncated, outputTruncated,
            resultLimitReached = hasMore && !outputTruncated, totalMatches = hasMore || scanTruncated ? (int?)null : found,
            notice = scanTruncated ? "Scan limit reached; narrow path or pattern." : hasMore ? $"Continue with offset={offset + paths.Count}." : "All matching paths returned." }, hasMore || scanTruncated);
    }

    [AgentTool("file_search"), Description("Search text using ripgrep, respecting .gitignore and skipping symlinks, binary files and build/cache directories. No file-size or scan-time cap. Returns 1-based line numbers and bounded context. nextOffset pages matches; linesTruncated/contextTruncated indicate clipped output. Requires installed rg.")]
    public async Task<ToolResult> SearchFilesAsync([Description("Nonempty text or ripgrep regex")] string pattern,
        [Description("File or directory path; '.' is workspace root")] string path = ".",
        [Description("Enable ripgrep regular expressions")] bool regex = false,
        [Description("Case-sensitive matching")] bool case_sensitive = false,
        [Description("File/path glob, e.g. *.cs or src/**/*.vue")] string file_pattern = "*",
        [Description("Nonnegative requested context lines before and after each match; context is clipped to fit the output budget")] int context_lines = 1,
        [Description("Positive requested matching-line count; output is paginated by budget")] int limit = 50,
        [Description("Nonnegative number of matching lines to skip")] int offset = 0,
        [Description("Bypass ignore rules and include build/cache directories")] bool include_ignored = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(pattern)) return ToolResult.Failure("pattern must be nonempty.");
        if (string.IsNullOrEmpty(file_pattern)) return ToolResult.Failure("file_pattern must be nonempty.");
        if (context_lines < 0) return ToolResult.Failure("context_lines must be nonnegative.");
        if (limit < 1) return ToolResult.Failure("limit must be positive.");
        if (offset < 0) return ToolResult.Failure("offset must be nonnegative.");
        var pageLimit = Math.Min(limit, ToolOutputWindow.MaxLines);
        var root = workspace.Resolve(path);
        if (!File.Exists(root) && !Directory.Exists(root)) return ToolResult.Failure("Search path does not exist.");
        var scope = Directory.Exists(root) ? root : Path.GetDirectoryName(root)!;
        var candidates = new List<(string Path, int Line, string Text)>(); var found = 0; var hasMore = false; var linesTruncated = false;
        var filter = GlobExpression(file_pattern, !case_sensitive);
        var pathFilter = file_pattern.Contains('/') || file_pattern.Contains('\\');
        var args = RipgrepArgs(include_ignored); args.AddRange(["--json", "--line-number"]);
        if (!regex) args.Add("--fixed-strings");
        if (!case_sensitive) args.Add("--ignore-case");
        // A positive rg --glob can override .gitignore. Filter matches in C# so
        // requesting '*.cs' cannot re-include ignored generated C# files.
        args.AddRange(["--", pattern, root]);
        var run = await RunRipgrepAsync(args, value =>
        {
            using var json = JsonDocument.Parse(value); var item = json.RootElement;
            if (item.GetProperty("type").GetString() != "match") return true;
            var data = item.GetProperty("data");
            if (!data.GetProperty("path").TryGetProperty("text", out var file) || file.GetString() is not { } target || !SafeDiscoveredPath(target, scope)) return true;
            if (!filter.IsMatch(pathFilter ? Path.GetRelativePath(scope, target).Replace('\\', '/') : Path.GetFileName(target))) return true;
            if (found++ < offset) return true;
            if (candidates.Count == pageLimit) { hasMore = true; return false; }
            var raw = data.GetProperty("lines").GetProperty("text").GetString() ?? "";
            var text = raw.TrimEnd('\r', '\n');
            linesTruncated |= text.Length > 1000;
            candidates.Add((target, data.GetProperty("line_number").GetInt32(), ToolResult.Clip(text, 1000)));
            return true;
        }, ct);
        if (run.Error != null) return ToolResult.Failure(run.Error);
        var matches = new List<object>(); var outputBytes = 0; var outputTruncated = false; var contextTruncated = false;
        string? cachedPath = null; string[] cachedLines = [];
        foreach (var match in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // rg locates the match; C# host supplies bounded surrounding text.
            if (cachedPath != match.Path)
            {
                if (!SafeDiscoveredPath(match.Path, scope)) continue;
                cachedLines = TextFileEdits.NormalizeLines(await File.ReadAllTextAsync(match.Path, ct)).Split('\n'); cachedPath = match.Path;
            }
            var start = (int)Math.Max(0L, (long)match.Line - 1 - context_lines);
            var end = (int)Math.Min(cachedLines.Length, (long)match.Line + context_lines);
            var context = new List<SearchContext>();
            var contextBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { path = match.Path, line = match.Line, text = match.Text }, ToolResult.JsonOptions)) + 512;
            // Add nearest lines first. A large requested context is accepted, but
            // the returned window fits the result budget and always includes the hit.
            for (var distance = 0; distance < Math.Max(match.Line - start, end - match.Line + 1); distance++)
            {
                var positions = distance == 0 ? new[] { match.Line - 1 } : new[] { match.Line - 1 - distance, match.Line - 1 + distance };
                var fits = true;
                foreach (var n in positions.Where(n => n >= start && n < end))
                {
                    linesTruncated |= cachedLines[n].Length > 500;
                    var entry = new SearchContext(n + 1, ToolResult.Clip(cachedLines[n], 500), n == match.Line - 1);
                    var cost = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry, ToolResult.JsonOptions));
                    if (contextBytes + cost > ToolOutputWindow.MaxBytes) { fits = false; break; }
                    context.Add(entry); contextBytes += cost;
                }
                if (!fits) break;
            }
            context.Sort((a, b) => a.line.CompareTo(b.line));
            contextTruncated |= context.Count < end - start;
            var row = new { path = Path.GetRelativePath(workspace.Root, match.Path).Replace('\\', '/'), line = match.Line, text = match.Text, context };
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(row, ToolResult.JsonOptions));
            if (outputBytes + size > ToolOutputWindow.MaxBytes)
            {
                if (matches.Count == 0) return ToolResult.Failure("A matching row exceeds the output budget; narrow the search path or reduce context_lines.");
                outputTruncated = true; hasMore = true; break;
            }
            outputBytes += size; matches.Add(row);
        }
        return ToolResult.Success(new { pattern, path, matches, nextOffset = offset + matches.Count, linesTruncated, contextTruncated, outputTruncated,
            matchLimitReached = hasMore && !outputTruncated, scanTruncated = false,
            notice = hasMore ? $"Continue with offset={offset + matches.Count}." : linesTruncated || contextTruncated ? "Text/context was clipped; use file_read at the reported line." : "Search complete." }, hasMore || linesTruncated || contextTruncated);
    }

    private sealed record SearchContext(int line, string text, bool match);

    private static List<string> RipgrepArgs(bool includeIgnored)
    {
        var args = new List<string> { "--no-config", "--hidden", "--no-follow", "--no-require-git", "--sort", "path", "--color", "never" };
        if (includeIgnored) args.Add("--no-ignore");
        else foreach (var name in SearchIgnored) args.AddRange(["--glob", "!**/" + name + "/**"]);
        return args;
    }

    private static Regex GlobExpression(string pattern, bool? ignoreCase = null)
    {
        var normalized = pattern.Replace('\\', '/'); var expression = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
            if (normalized[i] == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                i++;
                if (i + 1 < normalized.Length && normalized[i + 1] == '/') { i++; expression.Append("(?:.*/)?"); } else expression.Append(".*");
            }
            else expression.Append(normalized[i] == '*' ? "[^/]*" : normalized[i] == '?' ? "[^/]" : Regex.Escape(normalized[i].ToString()));
        return new Regex(expression.Append('$').ToString(), RegexOptions.NonBacktracking | ((ignoreCase ?? OperatingSystem.IsWindows()) ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    private static bool SafeDiscoveredPath(string path, string scope)
    {
        var full = Path.GetFullPath(path);
        if (!ExternalDirectoryPermissions.Contains(scope, full)) return false;
        for (var current = full; current != null && ExternalDirectoryPermissions.Contains(scope, current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static async IAsyncEnumerable<string> RipgrepRecords(StreamReader reader, char separator,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[8192]; var record = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < count; i++)
                if (buffer[i] == separator) { yield return record.ToString(); record.Clear(); }
                else record.Append(buffer[i]);
        }
        if (record.Length > 0) yield return record.ToString();
    }

    private static async Task<(string? Error, bool Stopped)> RunRipgrepAsync(List<string> args, Func<string, bool> consume, CancellationToken ct, char separator = '\n')
    {
        var executable = EnvironmentDoctor.FindExecutable(OperatingSystem.IsWindows() ? "rg.exe" : "rg");
        if (executable == null) return ("ripgrep (rg) is required. Install it or run /doctor install; search did not run.", false);
        using var process = new Process { StartInfo = BoundedProcess.StartInfo(executable, args) }; process.Start();
        var stderr = BoundedProcess.DrainAsync(process.StandardError, 4000, ct);
        var stopped = false;
        try
        {
            await foreach (var line in RipgrepRecords(process.StandardOutput, separator, ct))
                if (!consume(line)) { stopped = true; BoundedProcess.Kill(process); break; }
            await process.WaitForExitAsync(ct);
            var error = await stderr;
            return !stopped && process.ExitCode is not (0 or 1) ? ("ripgrep failed: " + error.Text, false) : (null, stopped);
        }
        finally
        {
            BoundedProcess.Kill(process);
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }
}
