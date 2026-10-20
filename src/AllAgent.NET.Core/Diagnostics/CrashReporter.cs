using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllAgent.NET.Diagnostics;

/// <summary>One crash bundle under the crash root.</summary>
public sealed record CrashReport
{
    public required string Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public required string Component { get; init; }
    public bool Fatal { get; init; }
    public required string ExceptionType { get; init; }
    public required string Message { get; init; }
    public required string Version { get; init; }
    public required string Folder { get; init; }
    public string MarkdownPath => Path.Combine(Folder, "report.md");
    public string JsonPath => Path.Combine(Folder, "report.json");
    public string UrlPath => Path.Combine(Folder, "report-url.txt");
    public string? DumpPath { get; init; }
    public required string IssueUrl { get; init; }
    public bool Notified { get; init; }
    public bool Reported { get; init; }
}

/// <summary>Knobs for a crash bundle; defaults match the installed application layout.</summary>
public sealed class CrashOptions
{
    public string? RootDirectory { get; init; }
    public string? LogPath { get; init; }
    public string RepositoryUrl { get; init; } = CrashReporter.DefaultRepository;
    public bool WriteDump { get; init; } = true;
    public bool FullDump { get; init; }
    public int MaxBundles { get; init; } = 20;
    public int LogTailBytes { get; init; } = 128 * 1024;
    public int MaxIssueUrlLength { get; init; } = 7000;
    public bool RedactUserProfile { get; init; } = true;
    public IReadOnlyDictionary<string, string?>? Extra { get; init; }
}

/// <summary>
/// Writes a self-contained crash bundle (report, log tail, optional minidump) and offers a
/// prefilled GitHub issue URL. Nothing is uploaded automatically: the user chooses whether to
/// open the issue and attach the bundle.
/// </summary>
public static class CrashReporter
{
    public const string DefaultRepository = "https://github.com/luolangaga/AllAgent.NET";

    private const string SchemaVersion = "1";
    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private static readonly object Gate = new();
    private static readonly Regex[] Redactions =
    [
        new(@"(?i)(\bbearer\s+)\S+", RegexOptions.CultureInvariant),
        new(@"(?i)(\bbasic\s+)[A-Za-z0-9+/=]{8,}", RegexOptions.CultureInvariant),
        new(@"(?i)((?:api[_-]?key|access[_-]?key|secret|token|password|passwd|pwd|client[_-]?secret|refresh[_-]?token)\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant),
        new(@"(?i)([?&](?:key|token|api_key|access_token|password)=)[^&\s]+", RegexOptions.CultureInvariant),
        new(@"(?i)(--(?:password|token|api-key|key)\s+)\S+", RegexOptions.CultureInvariant),
        new(@"\bsk-[A-Za-z0-9_\-]{12,}\b", RegexOptions.CultureInvariant),
        new(@"\b(?:gh[pousr]|github_pat)_[A-Za-z0-9_]{20,}\b", RegexOptions.CultureInvariant),
        new(@"\bAIza[0-9A-Za-z_\-]{20,}\b", RegexOptions.CultureInvariant),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant),
        new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant),
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b", RegexOptions.CultureInvariant),
    ];

    private static CrashOptions _options = new();
    private static string _component = "host";
    private static Action<CrashReport>? _onReported;
    private static int _installed;
    private static int _reporting;
    private static string? _lastFingerprint;
    private static DateTimeOffset _lastFingerprintAt;

    private static CrashOptions CurrentOptions => Volatile.Read(ref _options);
    private static string CurrentComponent => Volatile.Read(ref _component);

    /// <summary>Root that receives crash bundles.</summary>
    public static string RootDirectory => ResolveRoot(CurrentOptions);

    /// <summary>Default application log read into every bundle.</summary>
    public static string LogPath => ResolveLogPath(CurrentOptions);

    /// <summary>
    /// Registers process-wide handlers once. A GUI host or shell passes a callback so the
    /// report can be surfaced with a dialog when no console exists.
    /// </summary>
    public static void Install(string component, CrashOptions? options = null, Action<CrashReport>? onReported = null)
    {
        lock (Gate)
        {
            _component = string.IsNullOrWhiteSpace(component) ? "host" : component;
            _options = options ?? new CrashOptions();
            _onReported = onReported;
        }
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception
                ?? new InvalidOperationException("Unhandled non-Exception object: " + args.ExceptionObject);
            var report = Write(exception, CurrentComponent, fatal: true, "unhandled", CurrentOptions);
            if (report != null) Notify(report);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Unobserved task exceptions are logged for diagnosis, but they are not fatal and
            // never enter the startup prompt.
            Write(args.Exception, CurrentComponent, fatal: false, "unobserved-task", CurrentOptions);
            args.SetObserved();
        };
    }

    /// <summary>Writes a bundle for an exception the host caught itself.</summary>
    public static CrashReport? Report(Exception exception, string? component = null, bool fatal = true, CrashOptions? options = null, bool notify = true)
    {
        var report = Write(exception, component ?? CurrentComponent, fatal, fatal ? "fatal" : "manual", options ?? CurrentOptions);
        if (report != null && notify) Notify(report);
        return report;
    }

    /// <summary>Newest reports first, including non-fatal ones.</summary>
    public static IReadOnlyList<CrashReport> ListReports(int max = 100, CrashOptions? options = null)
        => LoadReports(options ?? CurrentOptions, max);

    /// <summary>Fatal bundles the user has not seen yet, newest first.</summary>
    public static IReadOnlyList<CrashReport> GetPendingReports(int max = 3, CrashOptions? options = null)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        return LoadReports(options ?? CurrentOptions, Math.Max(max, 20))
            .Where(report => report.Fatal && !report.Notified && !report.Reported && report.TimestampUtc >= cutoff)
            .Take(max)
            .ToArray();
    }

    public static void MarkNotified(CrashReport report) => WriteMarker(report, ".notified");

    public static void MarkReported(CrashReport report)
    {
        WriteMarker(report, ".notified");
        WriteMarker(report, ".reported");
    }

    /// <summary>Opens the prefilled GitHub issue page in the default browser.</summary>
    public static bool OpenIssue(CrashReport report)
    {
        try
        {
            if (!Uri.TryCreate(report.IssueUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            Process.Start(new ProcessStartInfo(report.IssueUrl) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Redact(string text) => Redact(text, CurrentOptions);

    private static CrashReport? Write(Exception exception, string component, bool fatal, string kind, CrashOptions options)
    {
        try
        {
            if (Interlocked.Exchange(ref _reporting, 1) != 0) return null;
            try
            {
                if (!fatal && IsRepeated(exception)) return null;
                return WriteCore(exception, component, fatal, kind, options);
            }
            finally
            {
                Volatile.Write(ref _reporting, 0);
            }
        }
        catch
        {
            // Crash reporting must never replace the original failure with another one.
            return null;
        }
    }

    private static CrashReport? WriteCore(Exception exception, string component, bool fatal, string kind, CrashOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N")[..12];
        var safeComponent = SafeName(component);
        var root = ResolveRoot(options);
        System.IO.Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"{now.ToLocalTime():yyyyMMdd-HHmmss}-{safeComponent}-{id}");
        System.IO.Directory.CreateDirectory(folder);

        var version = ResolveVersion();
        var context = BuildContext(options);
        var logTail = Redact(ReadLogTail(ResolveLogPath(options), options.LogTailBytes), options);
        var exceptionText = Redact(exception.ToString(), options);
        var report = new CrashReport
        {
            Id = id,
            TimestampUtc = now,
            Component = component,
            Fatal = fatal,
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = Clip(SingleLine(Redact(exception.Message, options)), 300),
            Version = version,
            Folder = folder,
            DumpPath = null,
            IssueUrl = string.Empty,
        };
        report = WithIssueUrl(report, context, exceptionText, options);
        // Write the text report before the minidump: a dump that stalls or fails must not
        // leave the user with no diagnostic bundle at all.
        WriteFiles(report, kind, context, exception, exceptionText, logTail, options);
        if (fatal)
        {
            try
            {
                var dumpPath = WriteDump(folder, options);
                if (dumpPath != null)
                {
                    report = WithIssueUrl(report with { DumpPath = dumpPath }, context, exceptionText, options);
                    WriteFiles(report, kind, context, exception, exceptionText, logTail, options);
                }
            }
            catch
            {
                // Keep the report already on disk.
            }
        }
        Prune(root, options.MaxBundles, folder);
        return report;
    }

    private static CrashReport WithIssueUrl(CrashReport report, IReadOnlyList<KeyValuePair<string, string>> context, string exceptionText, CrashOptions options)
        => report with { IssueUrl = BuildIssueUrl(report, options, BuildIssueBody(report, context, exceptionText, options)) };

    private static void WriteFiles(CrashReport report, string kind, IReadOnlyList<KeyValuePair<string, string>> context, Exception exception, string exceptionText, string logTail, CrashOptions options)
    {
        File.WriteAllText(report.JsonPath, BuildJson(report, kind, context, exception, options), new UTF8Encoding(false));
        File.WriteAllText(report.MarkdownPath, BuildMarkdown(report, context, exceptionText, logTail), new UTF8Encoding(false));
        File.WriteAllText(report.UrlPath, report.IssueUrl + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void Notify(CrashReport report)
    {
        try { _onReported?.Invoke(report); }
        catch { /* the callback only presents the report */ }
    }

    private static bool IsRepeated(Exception exception)
    {
        var signature = exception.GetType().FullName + "|" + exception.Message + "|" + TopFrame(exception);
        var now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            if (signature == _lastFingerprint && now - _lastFingerprintAt < TimeSpan.FromMinutes(5)) return true;
            _lastFingerprint = signature;
            _lastFingerprintAt = now;
            return false;
        }
    }

    private static string TopFrame(Exception exception)
    {
        var stack = exception.StackTrace;
        if (string.IsNullOrEmpty(stack)) return "";
        var end = stack.IndexOf('\n');
        return end < 0 ? stack : stack[..end];
    }

    private static string BuildJson(CrashReport report, string kind, IReadOnlyList<KeyValuePair<string, string>> context, Exception exception, CrashOptions options)
    {
        var contextValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in context) contextValues[key] = value;
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = report.Id,
            ["timestampUtc"] = report.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["component"] = report.Component,
            ["kind"] = kind,
            ["fatal"] = report.Fatal,
            ["version"] = report.Version,
            ["context"] = contextValues,
            ["exception"] = Describe(exception, options),
            ["files"] = new Dictionary<string, object?>
            {
                ["report"] = "report.md",
                ["dump"] = report.DumpPath == null ? null : "memory.dmp",
            },
            ["issueUrl"] = report.IssueUrl,
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object?> Describe(Exception exception, CrashOptions options)
    {
        var inner = new List<Dictionary<string, object?>>();
        for (var current = exception.InnerException; current != null && inner.Count < 8; current = current.InnerException)
        {
            inner.Add(new Dictionary<string, object?>
            {
                ["type"] = current.GetType().FullName ?? current.GetType().Name,
                ["message"] = Redact(current.Message, options),
                ["stackTrace"] = Redact(current.StackTrace ?? "", options),
                ["hresult"] = current.HResult,
            });
        }
        return new Dictionary<string, object?>
        {
            ["type"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["message"] = Redact(exception.Message, options),
            ["hresult"] = exception.HResult,
            ["source"] = Redact(exception.Source ?? "", options),
            ["stackTrace"] = Redact(exception.StackTrace ?? "", options),
            ["text"] = Redact(exception.ToString(), options),
            ["inner"] = inner,
        };
    }

    private static string BuildMarkdown(CrashReport report, IReadOnlyList<KeyValuePair<string, string>> context, string exceptionText, string logTail)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AllAgent.NET 崩溃报告 / Crash report");
        builder.AppendLine();
        builder.AppendLine("| 字段 | 值 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| 报告 ID | `{report.Id}` |");
        builder.AppendLine($"| 时间 (UTC) | {report.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| 组件 | {Cell(report.Component)} |");
        builder.AppendLine($"| 类型 | {(report.Fatal ? "fatal" : "non-fatal")} |");
        builder.AppendLine($"| 版本 | {Cell(report.Version)} |");
        foreach (var (key, value) in context) builder.AppendLine($"| {Cell(key)} | {Cell(value)} |");
        if (report.DumpPath != null) builder.AppendLine($"| 内存转储 | `{Cell(Path.GetFileName(report.DumpPath))}` |");
        builder.AppendLine();
        builder.AppendLine("## 异常 / Exception");
        builder.AppendLine();
        builder.AppendLine("````text");
        builder.AppendLine(exceptionText);
        builder.AppendLine("````");
        builder.AppendLine();
        builder.AppendLine("## 最近的日志 / Recent log tail");
        builder.AppendLine();
        builder.AppendLine("````text");
        builder.AppendLine(logTail);
        builder.AppendLine("````");
        builder.AppendLine();
        builder.AppendLine("## 提交 / Submit");
        builder.AppendLine();
        builder.AppendLine("1. 打开 `report-url.txt` 中的链接，或运行 `allagent crash open`。");
        builder.AppendLine("2. 附上本文件；如有 `memory.dmp`，建议压缩后一起上传。");
        builder.AppendLine("3. 提交前检查并删除路径、密钥等不想公开的信息。");
        builder.AppendLine();
        builder.AppendLine("> 报告由 AllAgent.NET 自动生成，不会自动上传。");
        return builder.ToString();
    }

    private static string BuildIssueBody(CrashReport report, IReadOnlyList<KeyValuePair<string, string>> context, string exceptionText, CrashOptions options)
    {
        var summary = context
            .Where(pair => pair.Key is "version" or "runtime" or "os" or "processArchitecture" or "workingDirectory")
            .Select(pair => $"- {pair.Key}: `{pair.Value}`");
        var builder = new StringBuilder();
        builder.AppendLine("### 崩溃摘要 / Crash summary");
        builder.AppendLine();
        builder.AppendLine("- 组件 / Component: `" + report.Component + "`");
        builder.AppendLine("- 异常 / Exception: `" + SingleLine(report.ExceptionType + ": " + report.Message) + "`");
        builder.AppendLine("- 时间 / Time (UTC): `" + report.TimestampUtc.ToString("O", CultureInfo.InvariantCulture) + "`");
        foreach (var line in summary) builder.AppendLine(line);
        builder.AppendLine();
        builder.AppendLine("### 复现步骤 / Steps to reproduce");
        builder.AppendLine();
        builder.AppendLine("1. ");
        builder.AppendLine();
        builder.AppendLine("### 堆栈 / Stack trace");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine(Clip(FirstLines(exceptionText, 80), 8000));
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("完整诊断报告保存在 `" + report.MarkdownPath + "`。提交前请附上 `report.md`" +
            (report.DumpPath == null ? "。" : "，并压缩上传 `memory.dmp`。"));
        builder.AppendLine();
        builder.AppendLine("> 由 AllAgent.NET 崩溃报告器生成；报告不会自动上传，请先检查敏感信息。");
        var body = Redact(builder.ToString(), options);
        if (options.RedactUserProfile) body = RedactProfile(body);
        return body;
    }

    private static string BuildIssueUrl(CrashReport report, CrashOptions options, string body)
    {
        var repository = (options.RepositoryUrl.Length == 0 ? DefaultRepository : options.RepositoryUrl).TrimEnd('/');
        var baseUrl = repository + "/issues/new";
        var summary = Clip(SingleLine(report.ExceptionType + ": " + report.Message), 120);
        var title = Escape($"[crash] {summary} ({report.Version})");
        var text = body;
        var url = Url(baseUrl, title, text);
        if (url.Length <= options.MaxIssueUrlLength) return url;
        for (var attempt = 0; attempt < 6 && url.Length > options.MaxIssueUrlLength; attempt++)
        {
            var keep = Math.Max(200, text.Length * options.MaxIssueUrlLength / Math.Max(url.Length, 1) - 200);
            text = text[..Math.Min(keep, text.Length)] + "\n\n...(已截断，完整内容见 report.md / truncated; see report.md)";
            url = Url(baseUrl, title, text);
        }
        return url;
    }

    private static string Url(string baseUrl, string title, string body)
        => baseUrl + "?title=" + title + "&body=" + Escape(body);

    private static string Escape(string value)
    {
        try { return Uri.EscapeDataString(value); }
        catch (UriFormatException)
        {
            // Exception text can contain an unpaired surrogate from a decoded file. Replace
            // those code units so the rest of the report and the issue URL still work.
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder.Append(character).Append(value[index + 1]);
                    index++;
                }
                else builder.Append(char.IsSurrogate(character) ? '\uFFFD' : character);
            }
            return Uri.EscapeDataString(builder.ToString());
        }
    }

    private static List<KeyValuePair<string, string>> BuildContext(CrashOptions options)
    {
        var context = new List<KeyValuePair<string, string>>
        {
            new("version", ResolveVersion()),
            new("runtime", RuntimeInformation.FrameworkDescription),
            new("os", RuntimeInformation.OSDescription),
            new("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString()),
            new("osArchitecture", RuntimeInformation.OSArchitecture.ToString()),
            new("process", (Assembly.GetEntryAssembly()?.GetName().Name ?? "AllAgent.NET") + " (" + Environment.ProcessId + ")"),
            new("workingDirectory", Environment.CurrentDirectory),
            new("commandLine", Redact(string.Join(' ', Environment.GetCommandLineArgs()), options)),
            new("startedUtc", StartTime()),
            new("managedMemory", GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture)),
        };
        if (options.Extra != null)
        {
            foreach (var (key, value) in options.Extra.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                context.Add(new KeyValuePair<string, string>(key, Redact(value ?? "", options)));
        }
        return context;
    }

    private static string StartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashReporter).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveRoot(CrashOptions options)
    {
        var configured = options.RootDirectory ?? Environment.GetEnvironmentVariable("ALLAGENT_CRASH_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, "AllAgent.NET", "crashes");
        return Path.Combine(Path.GetTempPath(), "AllAgent.NET", "crashes");
    }

    private static string ResolveLogPath(CrashOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.LogPath)) return Path.GetFullPath(options.LogPath);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, "AllAgent.NET", "allagent.log");
        return Path.Combine(Path.GetTempPath(), "AllAgent.NET", "allagent.log");
    }

    private static string ReadLogTail(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path) || maxBytes <= 0) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (start > 0)
            {
                var newline = text.IndexOf('\n');
                if (newline >= 0) text = text[(newline + 1)..];
            }
            return FirstLines(text, 600);
        }
        catch
        {
            return "";
        }
    }

    private static string? WriteDump(string folder, CrashOptions options)
    {
        if (!options.WriteDump || !OperatingSystem.IsWindows()) return null;
        var mode = Environment.GetEnvironmentVariable("ALLAGENT_CRASH_DUMP")?.Trim().ToLowerInvariant();
        if (mode is "0" or "none" or "off" or "false" or "no") return null;
        var full = options.FullDump || mode is "full";
        var path = Path.Combine(folder, "memory.dmp");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var type = MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules;
            if (full) type |= MiniDumpWithFullMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData;
            if (!MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, stream.SafeFileHandle.DangerousGetHandle(), type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                stream.Dispose();
                TryDelete(path);
                return null;
            }
            stream.Flush(true);
            var length = stream.Length;
            stream.Dispose();
            if (length > 0) return path;
            TryDelete(path);
            return null;
        }
        catch
        {
            TryDelete(path);
            return null;
        }
    }

    private static IReadOnlyList<CrashReport> LoadReports(CrashOptions options, int max)
    {
        try
        {
            var root = ResolveRoot(options);
            if (!System.IO.Directory.Exists(root)) return [];
            var reports = new List<CrashReport>();
            foreach (var folder in System.IO.Directory.EnumerateDirectories(root)
                         .OrderByDescending(path => path, StringComparer.Ordinal)
                         .Take(Math.Max(max, 20)))
            {
                var jsonPath = Path.Combine(folder, "report.json");
                try
                {
                    if (!File.Exists(jsonPath)) continue;
                    if (new FileInfo(jsonPath).Length > 2 * 1024 * 1024) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var rootElement = document.RootElement;
                    var exception = rootElement.TryGetProperty("exception", out var value) ? value : default;
                    var exceptionType = exception.ValueKind == JsonValueKind.Object && exception.TryGetProperty("type", out var type)
                        ? type.GetString() ?? "Unknown"
                        : "Unknown";
                    var message = exception.ValueKind == JsonValueKind.Object && exception.TryGetProperty("message", out var text)
                        ? text.GetString() ?? ""
                        : "";
                    var issueUrl = rootElement.TryGetProperty("issueUrl", out var url) && url.ValueKind == JsonValueKind.String
                        ? url.GetString()
                        : null;
                    var repository = (options.RepositoryUrl.Length == 0 ? DefaultRepository : options.RepositoryUrl).TrimEnd('/');
                    if (issueUrl == null || !issueUrl.StartsWith(repository + "/issues/new", StringComparison.OrdinalIgnoreCase))
                        issueUrl = repository + "/issues/new";
                    var dump = rootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object
                        && files.TryGetProperty("dump", out var dumpValue) && dumpValue.ValueKind == JsonValueKind.String
                        ? Path.Combine(folder, dumpValue.GetString() ?? "")
                        : null;
                    reports.Add(new CrashReport
                    {
                        Id = rootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : Path.GetFileName(folder),
                        TimestampUtc = rootElement.TryGetProperty("timestampUtc", out var timestamp)
                            && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                            ? parsed
                            : System.IO.Directory.GetLastWriteTimeUtc(folder),
                        Component = rootElement.TryGetProperty("component", out var component) ? component.GetString() ?? "" : "",
                        Fatal = rootElement.TryGetProperty("fatal", out var fatal) && fatal.ValueKind == JsonValueKind.True,
                        ExceptionType = exceptionType,
                        Message = Clip(SingleLine(message), 300),
                        Version = rootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "",
                        Folder = folder,
                        DumpPath = dump != null && File.Exists(dump) ? dump : null,
                        IssueUrl = issueUrl!,
                        Notified = File.Exists(Path.Combine(folder, ".notified")),
                        Reported = File.Exists(Path.Combine(folder, ".reported")),
                    });
                }
                catch
                {
                    // A half-written or foreign bundle must not stop the listing.
                }
            }
            return reports.OrderByDescending(report => report.TimestampUtc).Take(max).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void WriteMarker(CrashReport report, string name)
    {
        try { File.WriteAllText(Path.Combine(report.Folder, name), ""); }
        catch { /* markers are a convenience */ }
    }

    private static void Prune(string root, int max, string keep)
    {
        if (max <= 0) return;
        try
        {
            var folders = new DirectoryInfo(root).EnumerateDirectories()
                .Where(folder => !folder.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(folder => folder.LastWriteTimeUtc)
                .ToList();
            foreach (var folder in folders.Skip(Math.Max(0, max - 1)))
            {
                try { folder.Delete(true); } catch { /* locked bundles stay until the next crash */ }
            }
        }
        catch
        {
            // Retention is best effort.
        }
    }

    private static string Redact(string? text, CrashOptions options)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;
        foreach (var rule in Redactions)
            result = rule.Replace(result, match => match.Groups.Count > 1 && match.Groups[1].Success
                ? match.Groups[1].Value + "[REDACTED]"
                : "[REDACTED]");
        return result;
    }

    private static string RedactProfile(string text)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(profile)
                ? text
                : text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return text;
        }
    }

    private static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        var name = builder.ToString().Trim('-');
        return name.Length == 0 ? "host" : name[..Math.Min(name.Length, 24)];
    }

    private static string SingleLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    private static string Cell(string value) => SingleLine(value).Replace("|", "\\|");

    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Take(count));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
