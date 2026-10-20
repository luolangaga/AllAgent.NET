using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.CodeGraph;

/// <summary>Owns the in-process index. One instance per plugin host; every workspace is
/// serialized through its own gate.</summary>
public sealed class CodeGraphService : IAsyncDisposable
{
    private const int MaxOutputCharacters = 64_000;
    private const int MaxExcerptLines = 120;
    private const int MaxExcerptCharacters = 6_000;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(60);

    private readonly PluginSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, OperationState> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public CodeGraphService(PluginSettings settings) => _settings = settings;

    public bool AutoSync => !string.Equals(_settings.Get("autosync"), "false", StringComparison.OrdinalIgnoreCase);

    public Task<CodeGraphStatus> StatusAsync(string workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        workspace = ValidateWorkspace(workspace);
        var path = CodeIndex.IndexPath(workspace);
        var exists = File.Exists(path);
        var index = exists ? (SnapshotFor(workspace).Index ??= CodeIndex.Load(workspace)) : null;
        var file = exists ? new FileInfo(path) : null;
        var error = index == null && exists ? "索引文件无法读取，请重建索引。" : TreeSitterNative.Error;
        var detail = Detail(index, exists, path, file);
        return Task.FromResult(new CodeGraphStatus(
            workspace,
            index != null,
            exists ? path : null,
            file?.Length,
            file?.LastWriteTimeUtc,
            index?.Files.Count ?? 0,
            index?.Symbols.Count ?? 0,
            index?.References.Count ?? 0,
            detail,
            error));
    }

    public async Task<ExploreResult> ExploreAsync(string workspace, string query, CancellationToken ct)
    {
        workspace = ValidateWorkspace(workspace);
        if (ValidateQuery(query) is { } invalid) return new("", false, invalid, null);
        var gate = _gates.GetOrAdd(workspace, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var index = await EnsureIndexAsync(workspace, refresh: AutoSync, force: false, forceCheck: false, ct);
            var (output, truncated) = BuildExploreOutput(workspace, index, query);
            return new(output, truncated, null, index.BuiltAt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new("", false, ex.Message, null); }
        finally { gate.Release(); }
    }

    public CodeGraphOperation StartIndex(string workspace, IndexMode mode)
    {
        workspace = ValidateWorkspace(workspace);
        var operation = new OperationState(Guid.NewGuid().ToString("N"), mode.ToString().ToLowerInvariant(), workspace);
        _operations[operation.Id] = operation;
        _pending[operation.Id] = Task.Run(() => RunIndexAsync(operation, mode), CancellationToken.None);
        Prune();
        return operation.Snapshot();
    }

    public async Task<CodeGraphOperation> WaitAsync(string id, CancellationToken ct)
    {
        if (_pending.TryGetValue(id, out var task))
        {
            try { await task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancel(id); throw; }
        }
        return GetOperation(id) ?? throw new ArgumentException("操作不存在。", nameof(id));
    }

    public CodeGraphOperation? GetOperation(string id) =>
        _operations.TryGetValue(id, out var operation) ? operation.Snapshot() : null;

    public bool Cancel(string id)
    {
        if (!_operations.TryGetValue(id, out var operation)) return false;
        try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private async Task RunIndexAsync(OperationState operation, IndexMode mode)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, operation.Cancellation.Token);
        var gate = _gates.GetOrAdd(operation.Workspace, _ => new(1, 1));
        try
        {
            await gate.WaitAsync(linked.Token);
            try
            {
                operation.Append(mode == IndexMode.Rebuild ? "正在重建索引…\n" : "正在构建索引…\n");
                var index = await EnsureIndexAsync(operation.Workspace, refresh: true, force: mode == IndexMode.Rebuild, forceCheck: true, linked.Token);
                operation.Append($"完成：{index.Files.Count} 文件 · {index.Symbols.Count} 符号 · {index.References.Count} 引用\n");
                if (index.Notice is { Length: > 0 } notice) operation.Append(notice + "\n");
                operation.Complete();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { operation.Cancel(); }
        catch (Exception ex) { operation.Fail(ex.Message); }
    }

    private async Task<CodeIndex> EnsureIndexAsync(string workspace, bool refresh, bool force, bool forceCheck, CancellationToken ct)
    {
        var snapshot = SnapshotFor(workspace);
        if (!force && !forceCheck && snapshot.Index is { } cached && (!refresh || DateTime.UtcNow - snapshot.CheckedAt < TimeSpan.FromSeconds(2)))
            return cached;

        var index = force ? null : (snapshot.Index ?? CodeIndex.Load(workspace));
        index ??= new CodeIndex();
        index.Workspace = workspace;

        var files = CodeIndex.EnumerateFiles(workspace, out var truncated);
        var absoluteByRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (var absolute in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(absolute);
            if (!info.Exists) continue;
            var relative = CodeIndex.RelativePath(workspace, absolute);
            var language = CodeIndex.LanguageOf(relative);
            absoluteByRelative[relative] = absolute;
            seen.Add(relative);
            if (index.Files.TryGetValue(relative, out var existing)
                && existing.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                && existing.Size == info.Length
                && existing.Language == language)
                continue;
            changed.Add(relative);
        }

        var removed = 0;
        if (!truncated)
        {
            foreach (var stale in index.Files.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                index.Files.Remove(stale);
                removed++;
            }
        }

        var rebuildAll = force || index.Files.Count == 0 || changed.Count > Math.Max(20, absoluteByRelative.Count / 3);
        if (rebuildAll) index.Files.Clear();

        var targets = rebuildAll
            ? files.ToArray()
            : changed.Where(absoluteByRelative.ContainsKey).Select(relative => absoluteByRelative[relative]).ToArray();

        var watch = Stopwatch.StartNew();
        foreach (var absolute in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > BuildTimeout)
            {
                truncated = true;
                break;
            }
            var relative = CodeIndex.RelativePath(workspace, absolute);
            var info = new FileInfo(absolute);
            if (!info.Exists) continue;
            var language = CodeIndex.LanguageOf(relative);
            var entry = new CodeIndexFile
            {
                Path = relative,
                Language = language,
                ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                Size = info.Length,
            };
            if (language != "other" && info.Length <= CodeIndex.MaxParseBytes)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(absolute, ct);
                    entry = CodeScanner.Scan(relative, language, text, info.LastWriteTimeUtc.Ticks, info.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    entry.Error = ex.Message;
                }
            }
            index.Files[relative] = entry;
        }

        index.BuiltAt = DateTime.UtcNow;
        index.Rebuild();
        index.Notice = BuildNotice(truncated, index);
        if (rebuildAll || changed.Count > 0 || removed > 0) index.Save();
        snapshot.Index = index;
        snapshot.CheckedAt = DateTime.UtcNow;
        return index;
    }

    private static string? BuildNotice(bool truncated, CodeIndex index)
    {
        if (truncated) return "索引已截断：文件数或构建耗时超过上限，可缩小工作区后重建。";
        var errors = index.Files.Values.Count(file => file.Error != null);
        return errors > 0 ? $"{errors} 个文件解析失败，详见状态信息。" : null;
    }

    private (string Text, bool Truncated) BuildExploreOutput(string workspace, CodeIndex index, string query)
    {
        var symbols = index.Search(query);
        if (symbols.Count == 0) symbols = index.SearchFile(query);

        var builder = new StringBuilder();
        if (TreeSitterNative.Error is { Length: > 0 } nativeError) builder.AppendLine("Warning: " + nativeError);
        builder.AppendLine($"CodeGraph · {index.Files.Count} files · {index.Symbols.Count} symbols · {index.References.Count} refs");
        if (index.Notice is { Length: > 0 } notice) builder.AppendLine(notice);

        if (symbols.Count == 0)
        {
            var terms = CodeIndex.Tokenize(query);
            var files = index.Files.Keys
                .Where(path => terms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(20)
                .ToArray();
            if (files.Length > 0)
            {
                builder.AppendLine("Files:");
                foreach (var file in files) builder.AppendLine(file);
            }
            var matches = SearchText(workspace, index, query, 200);
            if (matches.Count > 0)
            {
                builder.AppendLine("Text matches (the query did not match a symbol):");
                foreach (var match in matches) builder.AppendLine(match);
            }
            if (files.Length == 0 && matches.Count == 0)
                builder.AppendLine("No symbols or text matches. Try a symbol name, file name, or shorter terms.");
            return (builder.ToString(), false);
        }

        var truncated = false;
        var emitted = 0;
        foreach (var symbol in symbols)
        {
            var chunk = FormatSymbol(workspace, index, symbol);
            if (builder.Length + chunk.Length > MaxOutputCharacters)
            {
                truncated = true;
                break;
            }
            builder.Append(chunk);
            if (++emitted >= 6) break;
        }
        return (builder.ToString(), truncated);
    }

    private static string FormatSymbol(string workspace, CodeIndex index, CodeSymbol symbol)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {symbol.Name} ({symbol.Kind}) · {symbol.File}:{symbol.StartLine}-{symbol.EndLine}");
        if (!string.IsNullOrWhiteSpace(symbol.Signature)) builder.AppendLine(symbol.Signature);
        if (ReadExcerpt(workspace, symbol) is { } excerpt)
        {
            builder.AppendLine("```");
            builder.AppendLine(excerpt);
            builder.AppendLine("```");
        }
        var allCallers = index.ReferencesTo(symbol.Name);
        if (allCallers.Count is > 0 and <= 100)
        {
            var callers = allCallers
                .Where(reference => reference.File != symbol.File || reference.Line < symbol.StartLine || reference.Line > symbol.EndLine)
                .DistinctBy(reference => (reference.File, reference.Line))
                .Take(8)
                .ToArray();
            if (callers.Length > 0)
                builder.AppendLine("Callers (name match): " + string.Join(", ", callers.Select(reference => $"{reference.File}:{reference.Line}")));
        }
        else if (allCallers.Count > 100)
        {
            var files = allCallers.Select(reference => reference.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            builder.AppendLine($"Callers: {allCallers.Count} references in {files} files (name match; too many to list)");
        }
        var callees = index.ReferencesIn(symbol).Select(reference => reference.Name)
            .Where(name => name.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (callees.Length > 0)
            builder.AppendLine("Calls: " + string.Join(", ", callees.Take(12)) + (callees.Length > 12 ? "…" : ""));
        var impact = allCallers.Select(reference => reference.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (impact > 0) builder.AppendLine($"Impact: {impact} file(s)");
        builder.AppendLine();
        return builder.ToString();
    }

    private static string? ReadExcerpt(string workspace, CodeSymbol symbol)
    {
        var path = Path.Combine(workspace, symbol.File);
        if (!File.Exists(path)) return null;
        try
        {
            var lines = File.ReadAllLines(path);
            if (symbol.StartLine < 1 || symbol.StartLine > lines.Length) return null;
            var start = symbol.StartLine - 1;
            var end = Math.Min(lines.Length, Math.Min(symbol.EndLine, symbol.StartLine + MaxExcerptLines - 1));
            return ToolResult.Clip(string.Join('\n', lines[start..end]), MaxExcerptCharacters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".jsonc", ".yml", ".yaml", ".xml", ".csproj", ".props", ".targets",
        ".sln", ".slnx", ".txt", ".css", ".scss", ".html", ".htm", ".svg", ".ps1", ".sh",
        ".bat", ".cmd", ".toml", ".ini", ".config", ".gradle", ".vue",
    };

    private static List<string> SearchText(string workspace, CodeIndex index, string query, int limit)
    {
        var terms = CodeIndex.Tokenize(query);
        if (terms.Count == 0) return [];
        var results = new List<string>();
        foreach (var file in index.Files.Values)
        {
            if (file.Size > CodeIndex.MaxParseBytes) continue;
            if (file.Language == "other" && !TextExtensions.Contains(Path.GetExtension(file.Path))) continue;
            var path = Path.Combine(workspace, file.Path);
            if (!File.Exists(path)) continue;
            try
            {
                var lines = File.ReadAllLines(path);
                for (var indexLine = 0; indexLine < lines.Length; indexLine++)
                {
                    if (!terms.Any(term => lines[indexLine].Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                    results.Add($"{file.Path}:{indexLine + 1}: {ToolResult.Clip(lines[indexLine].Trim(), 300)}");
                    if (results.Count >= limit) return results;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results;
    }

    private static string Detail(CodeIndex? index, bool exists, string path, FileInfo? file)
    {
        if (!exists) return "内置索引 · 未构建（首次 codegraph_explore 会自动构建）";
        if (index == null) return "内置索引 · 无法读取（请重建）";
        var builder = new StringBuilder();
        builder.Append($"内置索引 · {index.Files.Count} 文件 · {index.Symbols.Count:N0} 符号 · {index.References.Count:N0} 引用");
        var languages = index.Languages();
        if (languages.Count > 0) builder.Append('\n').Append(string.Join(" · ", languages));
        if (file != null) builder.Append('\n').Append($"{Path.GetFileName(path)} · {FormatBytes(file.Length)} · {file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (index.Notice is { Length: > 0 } notice) builder.Append('\n').Append(notice);
        var errors = index.Files.Values.Where(entry => entry.Error != null).Select(entry => $"{entry.Path}: {entry.Error}").Take(3).ToArray();
        if (errors.Length > 0) builder.Append('\n').Append(string.Join('\n', errors));
        if (TreeSitterNative.Error is { Length: > 0 } nativeError) builder.Append('\n').Append(nativeError);
        return builder.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };

    private Snapshot SnapshotFor(string workspace) => _snapshots.GetOrAdd(workspace, _ => new Snapshot());

    private void Prune()
    {
        foreach (var stale in _operations.Values
            .Where(operation => operation.State != "running")
            .OrderByDescending(operation => operation.StartedAt)
            .Skip(20))
        {
            _operations.TryRemove(stale.Id, out _);
            _pending.TryRemove(stale.Id, out _);
        }
    }

    public static string? ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "query 不能为空。";
        if (query.Length > 4_000) return "query 最长 4000 字符。";
        if (query[0] == '-') return "query 不能以 - 开头。";
        if (query.Any(char.IsControl)) return "query 不能包含控制字符。";
        return null;
    }

    private static string ValidateWorkspace(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace))
            throw new ArgumentException("需要工作区的绝对路径。", nameof(workspace));
        var full = Path.GetFullPath(workspace);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("工作区目录不存在：" + full);
        return full;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await Task.WhenAll(_pending.Values).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        _lifetime.Dispose();
    }

    private sealed class Snapshot
    {
        public CodeIndex? Index { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    private sealed class OperationState
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();

        public OperationState(string id, string kind, string workspace)
        {
            Id = id;
            Kind = kind;
            Workspace = workspace;
        }

        public string Id { get; }
        public string Kind { get; }
        public string Workspace { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public string State { get; private set; } = "running";
        public string? Error { get; private set; }
        public DateTime? FinishedAt { get; private set; }

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_sync)
            {
                _output.Append(text);
                if (_output.Length > 16_000) _output.Remove(0, _output.Length - 8_000);
            }
        }

        public void Complete() { lock (_sync) { State = "done"; FinishedAt = DateTime.UtcNow; } }
        public void Fail(string error) { lock (_sync) { State = "error"; Error = error; FinishedAt = DateTime.UtcNow; } }
        public void Cancel() { lock (_sync) { State = "cancelled"; FinishedAt = DateTime.UtcNow; } }

        public CodeGraphOperation Snapshot()
        {
            lock (_sync) return new(Id, Kind, Workspace, State, _output.ToString(), Error, StartedAt, FinishedAt);
        }
    }
}
