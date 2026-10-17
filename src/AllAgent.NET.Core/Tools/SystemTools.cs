using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

public partial class SystemTools(WorkspacePolicy workspace, RuntimeEnvironment environment, IReadOnlyDictionary<string, string> shellAllowlist, AllAgent.NET.Core.Services.WorkspaceCheckpoints? checkpoints = null, ExternalDirectoryPermissions? permissions = null, AllAgent.NET.Core.Services.BackgroundTasks? jobs = null, int fileReadMaxBytes = 20_000, int fileReadMaxLines = 2_000, string? powerShellModuleDirectory = null) : IAgentToolPreflight, IAgentToolAccess, IAgentToolApprovalPolicy
{
    public ToolApprovalPolicy GetApprovalPolicy(string name, JsonElement arguments)
    {
        if (name != "run_shell") return new();
        var exe = ToolArguments.Get<string>(arguments, "exe");
        return ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, out var resolved, out var preapproved)
            ? new(!preapproved, resolved)
            : new();
    }

    public async ValueTask<ToolAccessGrant?> RequestAccessAsync(string name, JsonElement arguments, ToolApprover? approver, CancellationToken ct)
    {
        var key = name == "run_shell" ? "working_directory" : name is "file_read" or "file_write" or "file_edit" or "file_search" or "everything_search" or "list_directory" ? "path" : null;
        if (key == null) return null;
        var target = workspace.ResolveForApproval(ToolArguments.Get(arguments, key, "."));
        if (workspace.FullAccess || !workspace.IsExternal(target)) return null;
        if (workspace.HasAccess(target)) return new(() => workspace.EnterRequest(target));
        var directory = name is "list_directory" or "run_shell" || name is "file_search" or "everything_search" && Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        var session = AgentExecutionScope.ConversationId ?? Guid.Empty;
        var request = new ToolApprovalRequest("external_directory", ToolRisk.Read, JsonSerializer.SerializeToElement(new { path = target, directory, operation = name }), "访问工作区外的路径")
        { RememberForSession = permissions == null ? null : () => permissions.Allow(session, directory) };
        if (approver == null || !await approver(request, ct)) throw new UnauthorizedAccessException("外部目录访问已拒绝；请勿重试或改用 Shell 绕过此决定。");
        ct.ThrowIfCancellationRequested();
        return new(() => workspace.EnterRequest(target));
    }
    public ToolResult? ValidateInvocation(string name, JsonElement arguments)
    {
        if (name != "run_shell")
        {
            if (name is "file_read" or "file_write" or "file_edit" or "file_search" or "everything_search" or "list_directory") workspace.ResolveForApproval(ToolArguments.Get(arguments, "path", "."));
            return null;
        }
        if (!environment.Ready) return ToolResult.Failure(
            $"Runtime environment checks failed: Python={(environment.Python == null ? "missing or unsupported" : "ready")}; embedded PowerShell SDK={(environment.PowerShell == null ? "missing or unsupported" : "ready")}. Run doctor for diagnostics. "
            + string.Join("; ", environment.Problems));
        var exe = ToolArguments.Get<string>(arguments, "exe");
        if (!ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, out _, out _))
            return ToolResult.Failure("Executable '" + exe + "' was not found on PATH or at an absolute path. Installed executables outside the trusted aliases can run after a one-time approval; configure --allow-shell alias=absolute-path to trust one without prompting.");
        var args = ToolArguments.Get<string[]>(arguments, "args", []);
        var timeout = ToolArguments.Get(arguments, "timeout_seconds", 0);
        if (timeout < 0) return ToolResult.Failure($"run_shell.timeout_seconds must be nonnegative; 0 means no timeout. Received {timeout}.");
        for (var index = 0; index < args.Length; index++)
            if (args[index] == null || args[index].Contains('\0'))
                return ToolResult.Failure($"run_shell.args[{index}] must be a string without NUL; use one literal string per argument.");
        workspace.ResolveForApproval(ToolArguments.Get(arguments, "working_directory", "."));
        return null;
    }
    [AgentTool("file_write", Risk = ToolRisk.Write), Description("Create or replace a UTF-8 text file after approval; external directories require separate access approval. Default refuses overwriting.")]
    public async Task<ToolResult> WriteAsync([Description("Relative or absolute file path; external paths require directory approval")] string path,
        [Description("UTF-8 text to write")] string content,
        [Description("Allow replacement of an existing file")] bool overwrite = false, CancellationToken ct = default)
    {
        var target = workspace.Resolve(path);
        using var mutation = await FileMutationQueue.EnterAsync(target, ct);
        workspace.Resolve(path);
        if (File.Exists(target) && !overwrite) return ToolResult.Failure("File exists. Explicit overwrite=true is required.");
        var before = File.Exists(target) ? await File.ReadAllTextAsync(target, ct) : "";
        return await PersistFileAsync(path, target, before, content, overwrite, false, null, ct);
    }

    private async Task<ToolResult> PersistFileAsync(string path, string target, string before, string content, bool overwrite,
        bool bom, byte[]? expectedBytes, CancellationToken ct)
    {
        var checkpointed = false;
        if (checkpoints != null)
        {
            try { _ = new WorkspacePolicy(workspace.Root).Resolve(path); checkpoints.BeforeWrite(path); checkpointed = true; }
            catch (UnauthorizedAccessException) when (workspace.HasAccess(target)) { checkpoints.MarkExternalAction(); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(bom), ct);
            workspace.Resolve(path);
            if (expectedBytes != null)
            {
                var current = await File.ReadAllBytesAsync(target, ct);
                if (!current.SequenceEqual(expectedBytes)) return ToolResult.Failure("File changed during editing; reread before retrying. No edit applied.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite);
            if (checkpointed) checkpoints?.AfterWrite(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return ToolResult.Success(new { path, characters = content.Length, diff = FileChangePreview.Create(before, content) });
    }

    [AgentTool("file_edit", Risk = ToolRisk.Write), Description("Apply one or more targeted replacements to the same original file. Prefer edits[]. Each old_string must be unique and edits must not overlap. Exact matching first, then limited whitespace/Unicode normalization. Preserves BOM and line ending style. Rejects ambiguity and applies the entire batch or nothing.")]
    public async Task<ToolResult> EditAsync([Description("Relative or absolute file path; external paths require directory approval")] string path,
        [Description("Array of {old_string, new_string} replacements against the original file. Merge nearby or overlapping edits.")] FileEdit[] edits,
        CancellationToken ct = default)
    {
        if (edits == null || edits.Length < 1) return ToolResult.Failure("edits must contain at least one replacement.");
        for (var i = 0; i < edits.Length; i++)
            if (edits[i] == null || string.IsNullOrEmpty(edits[i].old_string) || edits[i].new_string == null)
                return ToolResult.Failure($"edits[{i}].old_string must be nonempty and new_string must be a string; no edit applied.");
        var target = workspace.Resolve(path);
        using var mutation = await FileMutationQueue.EnterAsync(target, ct);
        workspace.Resolve(path);
        if (!File.Exists(target)) return ToolResult.Failure("File not found; use file_write to create a new file.");
        var bytes = await File.ReadAllBytesAsync(target, ct);
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string content;
        try { content = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { return ToolResult.Failure("file_edit supports UTF-8 text only; the file was not modified. Convert a copy from its original encoding (for example GBK or UTF-16) to UTF-8, verify the text, then retry."); }
        if (content.Contains('\0')) return ToolResult.Failure("File contains NUL bytes and is not supported UTF-8 text; no edit applied.");
        var applied = TextFileEdits.Apply(content, edits);
        if (applied.Error != null) return ToolResult.Failure(applied.Error);
        var result = await PersistFileAsync(path, target, content, applied.Content!, true, bom, bytes, ct);
        return result.Ok ? result with { Data = new { path, characters = applied.Content!.Length, replacements = edits.Length,
            fuzzyMatch = applied.Fuzzy, diff = FileChangePreview.Create(content, applied.Content!) } } : result;
    }

    [AgentTool("list_directory"), Description("List a bounded page of entries in the workspace or an approved external directory without following links. Use path='.' for the workspace root, not '/' or an empty path.")]
    public Task<ToolResult> ListAsync([Description("Relative or absolute directory path; external paths require directory approval; '.' is the workspace root")] string path = ".",
        [Description("Nonnegative entries to skip after filtering")] int offset = 0, [Description("Positive requested entry count; output is paginated by budget")] int limit = 50, CancellationToken ct = default,
        [Description("Include normally hidden build/dependency directories: bin, obj, .git, node_modules, .venv, __pycache__; default false")] bool include_ignored = false)
    {
        ct.ThrowIfCancellationRequested();
        if (offset < 0 || limit < 1) return Task.FromResult(ToolResult.Failure("offset must be nonnegative and limit must be positive."));
        var pageLimit = Math.Min(limit, ToolOutputWindow.MaxLines);
        var ignored = new HashSet<string>(SearchIgnored, StringComparer.OrdinalIgnoreCase);
        var candidates = Directory.EnumerateFileSystemEntries(workspace.Resolve(path))
            .Where(p => include_ignored || !Directory.Exists(p) || !ignored.Contains(Path.GetFileName(p)))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(offset).Take(pageLimit + 1).ToArray();
        var entries = new List<object>(); var bytes = 0; var outputTruncated = false;
        foreach (var item in candidates.Take(pageLimit))
        {
            ct.ThrowIfCancellationRequested();
            var row = new { name = Path.GetFileName(item), directory = Directory.Exists(item), link = (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0 };
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(row, ToolResult.JsonOptions));
            if (bytes + size > ToolOutputWindow.MaxBytes) { outputTruncated = true; break; }
            entries.Add(row); bytes += size;
        }
        var hasMore = outputTruncated || candidates.Length > pageLimit;
        return Task.FromResult(ToolResult.Success(new
        {
            path, nextOffset = offset + entries.Count, entries, outputTruncated,
            entryLimitReached = candidates.Length > pageLimit,
            notice = hasMore ? $"Continue with offset={offset + entries.Count}." : "Directory listing complete."
        }, hasMore));
    }

    [AgentTool("run_shell", Risk = ToolRisk.Execute), Description("Run an installed executable by name or absolute path with literal arguments through the embedded PowerShell SDK host. Trusted aliases run directly; other installed executables require one-time approval. No command-string interpolation.")]
    public async Task<ToolResult> RunShellAsync([Description("Installed executable name on PATH or fully qualified executable path; untrusted programs require per-call approval")] string exe,
        [Description("Literal argument array; each item is passed separately")] string[] args,
        [Description("Relative or absolute working directory; external paths require directory approval")] string working_directory = ".",
        [Description("Optional timeout in seconds; 0 (default) means no timeout. Any positive value is accepted.")] int timeout_seconds = 0, CancellationToken ct = default,
        [Description("Return a background task ID immediately; inspect with shell_status and cancel with cancel_shell. The process is stopped on timeout or host shutdown.")] bool background = false)
    {
        var rejected = ValidateInvocation("run_shell", JsonSerializer.SerializeToElement(new { exe, args, working_directory, timeout_seconds, background }));
        if (rejected != null) return rejected;
        if (!ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, out var executable, out _)) return ToolResult.Failure("Executable is no longer available at the resolved path.");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exe = executable, args })));
        // Constant trusted PowerShell source; user arguments are decoded as data.
        var info = BoundedProcess.StartInfo(environment.PowerShell!, ["--execute", payload], workspace.Resolve(working_directory));
        if (powerShellModuleDirectory != null) info.Environment["ALLAGENT_PS_MODULE_ROOT"] = powerShellModuleDirectory;
        info.RedirectStandardInput = true;
        checkpoints?.MarkExternalAction();
        if (background)
        {
            if (jobs == null) return ToolResult.Failure("This host does not support background shell jobs.");
            var taskId = jobs.Start(exe + " " + string.Join(" ", args), AgentExecutionScope.ConversationId ?? Guid.Empty, "shell", (id, token) => RunBackgroundShell(info, timeout_seconds, (text, error) => jobs.AppendOutput(id, error ? "[stderr] " + text : text), token));
            return ToolResult.Success(new { taskId, background = true });
        }
        var result = await BoundedProcess.RunAsync(info, ShellTimeout(timeout_seconds), ct, stripAnsi: true, tailOutputDirectory: OutputDirectory());
        return new(result.ExitCode == 0 && !result.TimedOut, new { exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr,
            timedOut = result.TimedOut, stdoutPath = result.StdoutPath, stderrPath = result.StderrPath,
            notice = result.Truncated ? "Showing output tail. Read stdoutPath/stderrPath with file_read for full logs." : null },
            result.TimedOut ? $"Executable exceeded {timeout_seconds} seconds; partial output is preserved." : result.ExitCode == 0 ? null : "Executable exited with an error.", result.Truncated);
    }

    [AgentTool("shell_status"), Description("Inspect an existing background shell task; this does not launch the command again. Wait briefly for completion instead of repeatedly polling or restarting run_shell.")]
    public async Task<ToolResult> ShellStatusAsync([Description("Background shell task ID returned by run_shell")] string id,
        [Description("Host waits for completion without model polling, up to 0–60 seconds. Defaults to 60. Use 0 only for an explicit progress check; unfinished output stays in the terminal, completed output is returned.")] int wait_seconds = 60, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (wait_seconds is < 0 or > 60) return ToolResult.Failure("wait_seconds must be between 0 and 60.");
        var job = jobs?.Get(id);
        if (job?.Kind != "shell") return ToolResult.Failure("Shell task not found.");
        if (wait_seconds > 0 && job.State is AllAgent.NET.Core.Services.BackgroundTaskState.Running or AllAgent.NET.Core.Services.BackgroundTaskState.Queued)
        {
            try { job = await jobs!.WaitAsync(id, ct).WaitAsync(TimeSpan.FromSeconds(wait_seconds), ct); }
            catch (TimeoutException) { job = jobs!.Get(id); }
        }
        // Live output belongs to the host UI. Repeated unfinished snapshots must not
        // resend the growing output and step history to the model.
        return ToolResult.Success(job.State is AllAgent.NET.Core.Services.BackgroundTaskState.Running or AllAgent.NET.Core.Services.BackgroundTaskState.Queued
            ? job with { Output = "", Steps = [], FinalOutput = null }
            : job);
    }
    public Task<ToolResult> ShellStatusAsync(string id, CancellationToken ct) => ShellStatusAsync(id, 60, ct);

    [AgentTool("cancel_shell"), Description("Stop a background shell process and its descendants.")]
    public Task<ToolResult> CancelShellAsync([Description("Background shell task ID")] string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (jobs?.Get(id).Kind != "shell") return Task.FromResult(ToolResult.Failure("Shell task not found."));
        jobs.Cancel(id);
        return Task.FromResult(ToolResult.Success(new { cancelled = id }));
    }

    private string OutputDirectory() => workspace.Resolve(Path.Combine(workspace.Root, ".allagent", "tool-output"));
    private static TimeSpan ShellTimeout(int seconds) => seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);

    private async IAsyncEnumerable<AllAgent.NET.Core.Services.AgentEvent> RunBackgroundShell(System.Diagnostics.ProcessStartInfo info, int seconds, Action<string, bool> onOutput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var result = await BoundedProcess.RunAsync(info, ShellTimeout(seconds), ct, onOutput: onOutput, stripAnsi: true, tailOutputDirectory: OutputDirectory());
        if (result.Truncated) yield return new AllAgent.NET.Core.Services.TokenDelta($"\n完整输出：{result.StdoutPath ?? result.StderrPath}");
        yield return new AllAgent.NET.Core.Services.TokenDelta($"\n退出码 {result.ExitCode}" + (result.Truncated ? "\n[输出已截断]" : ""));
        if (result.TimedOut) yield return new AllAgent.NET.Core.Services.AgentError($"Process exceeded {seconds} seconds; partial output is preserved.");
        else if (result.ExitCode != 0) yield return new AllAgent.NET.Core.Services.AgentError($"退出码 {result.ExitCode}");
        yield return new AllAgent.NET.Core.Services.AgentDone(result.ExitCode == 0 ? "stop" : "error");
    }
}
