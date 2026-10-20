using System.Collections.Concurrent;
using System.Text;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.CodeGraph;

/// <summary>Owns the codegraph CLI process, index operations and their logs. One instance per
/// plugin host; every workspace is serialized through its own gate.</summary>
public sealed class CodeGraphService : IAsyncDisposable
{
    private const string PackageVersion = "1.6.2";
    private static readonly TimeSpan ExploreTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private readonly string _dataDirectory;
    private readonly PluginSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, OperationState> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastSync = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime Expires, string? Path, string? Version)? _detected;

    public CodeGraphService(string dataDirectory, PluginSettings settings)
    {
        _dataDirectory = dataDirectory;
        _settings = settings;
    }

    public bool AutoSync => !string.Equals(_settings.Get("autosync"), "false", StringComparison.OrdinalIgnoreCase);
    public bool Telemetry => string.Equals(_settings.Get("telemetry"), "true", StringComparison.OrdinalIgnoreCase);

    private CodeGraphLocator Locator() => new(() => _settings.Get("path"), () => Telemetry);

    private string LogsDirectory
    {
        get
        {
            var directory = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public async Task<(string? Path, string? Version)> DetectAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_detected is { } cached && cached.Expires > now) return (cached.Path, cached.Version);
        var locator = Locator();
        var path = locator.Resolve();
        string? version = null;
        if (path != null)
        {
            try { version = await locator.VersionAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { path = null; }
            if (version == null) path = null;
        }
        _detected = (now.AddSeconds(10), path, version);
        return (path, version);
    }

    public async Task<CodeGraphStatus> StatusAsync(string workspace, CancellationToken ct)
    {
        workspace = ValidateWorkspace(workspace);
        var (path, version) = await DetectAsync(ct);
        var indexPath = IndexPath(workspace);
        var database = DatabasePath(workspace);
        var indexed = File.Exists(database);
        string? detail = null;
        string? error = null;
        if (path != null && indexed)
        {
            try
            {
                var output = await BoundedProcess.RunAsync(Locator().StartInfo(["status", workspace], workspace), StatusTimeout, ct, limit: 8_000, stripAnsi: true);
                detail = output.ExitCode == 0 ? output.Stdout.Trim() : null;
                if (output.ExitCode != 0) error = Trim(output.Stderr + output.Stdout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        }
        var file = File.Exists(database) ? new FileInfo(database) : null;
        return new(workspace, path != null, path, version, indexed, indexPath, file?.Length, file?.LastWriteTimeUtc, detail, error);
    }

    public async Task<ExploreResult> ExploreAsync(string workspace, string query, CancellationToken ct)
    {
        workspace = ValidateWorkspace(workspace);
        if (ValidateQuery(query) is { } invalid) return new("", false, invalid, null);
        var (path, _) = await DetectAsync(ct);
        if (path == null)
            return new("", false, "未找到 codegraph。请先运行 /ext codegraph install --yes，或在设置页安装 CodeGraph。", null);
        if (!File.Exists(DatabasePath(workspace)))
            return new("", false, "当前工作区还没有 CodeGraph 索引。请运行 /ext codegraph init，或在设置页构建索引。", null);

        var gate = _gates.GetOrAdd(workspace, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (AutoSync && ShouldSync(workspace))
            {
                try { await RunSyncCoreAsync(workspace, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* a stale index is still readable */ }
            }
            ProcessOutput output;
            try
            {
                output = await BoundedProcess.RunAsync(Locator().StartInfo(["explore", query], workspace), ExploreTimeout, ct,
                    limit: 64_000, stripAnsi: true, tailOutputDirectory: LogsDirectory);
            }
            catch (TimeoutException)
            {
                return new("", false, "CodeGraph explore 超时（120 秒）；请缩小查询范围或重试。", LastSync(workspace));
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                return new("", false, ex.Message, LastSync(workspace));
            }
            if (output.ExitCode != 0)
                return new("", output.Truncated, Trim(output.Stderr + output.Stdout), LastSync(workspace));
            return new(output.Stdout.TrimEnd(), output.Truncated, null, LastSync(workspace));
        }
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

    public CodeGraphOperation StartInstall()
    {
        var operation = new OperationState(Guid.NewGuid().ToString("N"), "install", "");
        _operations[operation.Id] = operation;
        _pending[operation.Id] = Task.Run(() => RunInstallAsync(operation), CancellationToken.None);
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

    public async Task<InstallResult> InstallAsync(CancellationToken ct)
    {
        var output = new StringBuilder();
        var result = await InstallCoreAsync(ct, text => { lock (output) output.Append(text); });
        return result with { Output = output.ToString() };
    }

    public async Task<string> UnlockAsync(string workspace, CancellationToken ct)
    {
        workspace = ValidateWorkspace(workspace);
        var (path, _) = await DetectAsync(ct);
        if (path == null) return "未安装 CodeGraph。";
        var output = await BoundedProcess.RunAsync(Locator().StartInfo(["unlock", workspace], workspace), TimeSpan.FromSeconds(30), ct, limit: 4_000, stripAnsi: true);
        return (output.Stdout + output.Stderr).Trim();
    }

    private async Task RunIndexAsync(OperationState operation, IndexMode mode)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, operation.Cancellation.Token);
        var ct = linked.Token;
        var workspace = operation.Workspace;
        var gate = _gates.GetOrAdd(workspace, _ => new(1, 1));
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                var exists = File.Exists(DatabasePath(workspace));
                var args = mode switch
                {
                    IndexMode.Rebuild => new[] { "index", workspace, "--force" },
                    IndexMode.Sync => new[] { "sync", workspace },
                    _ => exists ? new[] { "sync", workspace } : new[] { "init", workspace }
                };
                operation.Append("$ codegraph " + string.Join(' ', args) + "\n");
                var output = await BoundedProcess.RunAsync(Locator().StartInfo(args, workspace),
                    mode == IndexMode.Sync ? SyncTimeout : IndexTimeout, ct,
                    limit: 20_000, onOutput: (text, _) => operation.Append(text), stripAnsi: true, tailOutputDirectory: LogsDirectory);
                operation.LogPath = output.StdoutPath ?? output.StderrPath;
                if (output.ExitCode != 0) operation.Fail(Trim(output.Stderr + output.Stdout));
                else
                {
                    _lastSync[workspace] = DateTime.UtcNow;
                    operation.Complete();
                }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { operation.Cancel(); }
        catch (Exception ex) { operation.Fail(ex.Message); }
    }

    private async Task RunInstallAsync(OperationState operation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, operation.Cancellation.Token);
        try
        {
            var result = await InstallCoreAsync(linked.Token, text => operation.Append(text));
            if (result.Installed) operation.Complete();
            else operation.Fail(result.Error ?? "安装失败。");
        }
        catch (OperationCanceledException) { operation.Cancel(); }
        catch (Exception ex) { operation.Fail(ex.Message); }
    }

    private async Task<InstallResult> InstallCoreAsync(CancellationToken ct, Action<string> onOutput)
    {
        var (path, version) = await DetectAsync(ct);
        if (path != null) return new(true, path, version, "", null);

        ProcessOutput output;
        var npm = EnvironmentDoctor.FindExecutable("npm.cmd") ?? EnvironmentDoctor.FindExecutable("npm");
        if (npm != null)
        {
            onOutput("$ npm install -g @colbymchenry/codegraph@" + PackageVersion + "\n");
            output = await RunCommandAsync(npm, ["install", "-g", "@colbymchenry/codegraph@" + PackageVersion], ct, InstallTimeout, onOutput);
        }
        else if (OperatingSystem.IsWindows())
        {
            var powershell = EnvironmentDoctor.FindExecutable("powershell.exe") ?? "powershell.exe";
            const string command = "irm https://raw.githubusercontent.com/colbymchenry/codegraph/main/install.ps1 | iex";
            onOutput("$ powershell -NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"\n");
            output = await RunCommandAsync(powershell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command], ct, InstallTimeout, onOutput);
        }
        else
        {
            throw new InvalidOperationException("请安装 Node.js/npm，或运行 codegraph 官方安装脚本后再试。");
        }

        RefreshPath();
        _detected = null;
        var (installedPath, installedVersion) = await DetectAsync(ct);
        if (installedPath == null)
            return new(false, null, null, output.Stdout, Trim(output.Stderr + output.Stdout));
        _settings.Set("path", installedPath);
        return new(true, installedPath, installedVersion, output.Stdout, null);
    }

    private async Task RunSyncCoreAsync(string workspace, CancellationToken ct)
    {
        var output = await BoundedProcess.RunAsync(Locator().StartInfo(["sync", workspace], workspace), SyncTimeout, ct, limit: 8_000, stripAnsi: true);
        if (output.ExitCode == 0) _lastSync[workspace] = DateTime.UtcNow;
    }

    private bool ShouldSync(string workspace) =>
        AutoSync && (!_lastSync.TryGetValue(workspace, out var last) || DateTime.UtcNow - last > TimeSpan.FromSeconds(2));

    private DateTime? LastSync(string workspace) => _lastSync.TryGetValue(workspace, out var last) ? last : null;

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

    public static string IndexPath(string workspace)
    {
        var configured = Environment.GetEnvironmentVariable("CODEGRAPH_DIR");
        var directory = string.IsNullOrWhiteSpace(configured) ? ".codegraph" : configured.Trim();
        return Path.GetFullPath(Path.IsPathFullyQualified(directory) ? directory : Path.Combine(workspace, directory));
    }

    public static string DatabasePath(string workspace) => Path.Combine(IndexPath(workspace), "codegraph.db");

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

    private static async Task<ProcessOutput> RunCommandAsync(string executable, string[] args, CancellationToken ct, TimeSpan timeout, Action<string> onOutput)
    {
        var info = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? BoundedProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } command ? command : "cmd.exe",
                ["/d", "/s", "/c", "call", executable, .. args])
            : BoundedProcess.StartInfo(executable, args);
        info.Environment["NO_COLOR"] = "1";
        return await BoundedProcess.RunAsync(info, timeout, ct, limit: 20_000, onOutput: (text, _) => onOutput(text), stripAnsi: true);
    }

    private static void RefreshPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        Environment.SetEnvironmentVariable("PATH", string.Join(';', new[] { current, machine, user }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string Trim(string text) => ToolResult.Clip(text.Trim(), 2_000);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await Task.WhenAll(_pending.Values).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        _lifetime.Dispose();
    }

    private sealed class OperationState
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();
        public OperationState(string id, string kind, string workspace)
        {
            Id = id; Kind = kind; Workspace = workspace;
        }
        public string Id { get; }
        public string Kind { get; }
        public string Workspace { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public string State { get; private set; } = "running";
        public string? Error { get; private set; }
        public string? LogPath { get; set; }
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
            lock (_sync) return new(Id, Kind, Workspace, State, _output.ToString(), Error, LogPath, StartedAt, FinishedAt);
        }
    }
}
