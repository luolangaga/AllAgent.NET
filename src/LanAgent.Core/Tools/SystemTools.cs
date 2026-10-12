using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.Tools;

public partial class SystemTools(WorkspacePolicy workspace, RuntimeEnvironment environment, IReadOnlyDictionary<string, string> shellAllowlist, LanAgent.Core.Services.WorkspaceCheckpoints? checkpoints = null, ExternalDirectoryPermissions? permissions = null, LanAgent.Core.Services.BackgroundTasks? jobs = null) : IAgentToolPreflight, IAgentToolAccess, IAgentToolApprovalPolicy
{
    public ToolApprovalPolicy GetApprovalPolicy(string name, JsonElement arguments)
    {
        if (name != "run_shell") return new();
        var exe = ToolArguments.Get<string>(arguments, "exe");
        return ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, environment.PowerShell, out var resolved, out var preapproved)
            ? new(!preapproved, resolved)
            : new();
    }

    public async ValueTask<ToolAccessGrant?> RequestAccessAsync(string name, JsonElement arguments, ToolApprover? approver, CancellationToken ct)
    {
        var key = name == "run_shell" ? "working_directory" : name is "file_read" or "file_write" or "file_edit" or "list_directory" ? "path" : null;
        if (key == null) return null;
        var target = workspace.ResolveForApproval(ToolArguments.Get(arguments, key, "."));
        if (workspace.FullAccess || !workspace.IsExternal(target)) return null;
        if (workspace.HasAccess(target)) return new(() => workspace.EnterRequest(target));
        var directory = name is "list_directory" or "run_shell" ? target : Path.GetDirectoryName(target)!;
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
            if (name is "file_read" or "file_write" or "file_edit" or "list_directory") workspace.ResolveForApproval(ToolArguments.Get(arguments, "path", "."));
            return null;
        }
        if (!environment.Ready) return ToolResult.Failure("Python and PowerShell 7.6 must pass environment checks first.");
        var exe = ToolArguments.Get<string>(arguments, "exe");
        if (!ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, environment.PowerShell, out _, out _))
            return ToolResult.Failure("Executable '" + exe + "' was not found on PATH or at an absolute path. Installed executables outside the trusted aliases can run after a one-time approval; configure --allow-shell alias=absolute-path to trust one without prompting.");
        var args = ToolArguments.Get<string[]>(arguments, "args", []);
        var timeout = ToolArguments.Get(arguments, "timeout_seconds", 30);
        if (timeout < 1 || timeout > (ToolArguments.Get(arguments, "background", false) ? 86400 : 120) || args.Length > 100 || args.Any(a => a == null || a.Length > 8000 || a.Contains('\0')))
            return ToolResult.Failure("Invalid arguments or timeout.");
        workspace.ResolveForApproval(ToolArguments.Get(arguments, "working_directory", "."));
        return null;
    }
    [AgentTool("file_read"), Description("Read a UTF-8 text file in the workspace or an approved external directory. Results are capped; use offset to page through large files.")]
    public async Task<ToolResult> ReadAsync([Description("Relative or absolute file path; external paths require directory approval")] string path,
        [Description("Character offset, zero based")] int offset = 0, [Description("Maximum characters, 1–20000")] int limit = ToolResult.MaxOutputCharacters, CancellationToken ct = default)
    {
        if (offset < 0 || offset > 1000000 || limit is < 1 or > ToolResult.MaxOutputCharacters) return ToolResult.Failure("Invalid offset or limit.");
        using var reader = new StreamReader(workspace.Resolve(path), Encoding.UTF8, true);
        var buffer = new char[Math.Min(8192, limit)];
        var skipped = 0;
        while (skipped < offset)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, offset - skipped)), ct);
            if (count == 0) break;
            skipped += count;
        }
        var text = new char[limit + 1];
        var read = 0;
        while (read < text.Length)
        {
            var count = await reader.ReadAsync(text.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }
        var length = Math.Min(read, limit);
        return ToolResult.Success(new { path, content = new string(text, 0, length), offset = skipped, nextOffset = skipped + length }, read > limit);
    }

    [AgentTool("file_write", Risk = ToolRisk.Write), Description("Create or replace a UTF-8 text file after approval; external directories require separate access approval. Default refuses overwriting.")]
    public async Task<ToolResult> WriteAsync([Description("Relative or absolute file path; external paths require directory approval")] string path,
        [Description("UTF-8 text, at most 100000 characters")] string content,
        [Description("Allow replacement of an existing file")] bool overwrite = false, CancellationToken ct = default)
    {
        if (content.Length > 100000) return ToolResult.Failure("Content exceeds 100000 characters.");
        var target = workspace.Resolve(path);
        if (File.Exists(target) && !overwrite) return ToolResult.Failure("File exists. Explicit overwrite=true is required.");
        if (File.Exists(target) && new FileInfo(target).Length > 1000000) return ToolResult.Failure("File exceeds the diff/edit size limit.");
        var before = File.Exists(target) ? await File.ReadAllTextAsync(target, ct) : "";
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
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct);
            workspace.Resolve(path);
            File.Move(temporary, target, overwrite);
            if (checkpointed) checkpoints?.AfterWrite(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return ToolResult.Success(new { path, characters = content.Length, diff = FileChangePreview.Create(before, content) });
    }

    [AgentTool("file_edit", Risk = ToolRisk.Write), Description("Replace exactly one matching text fragment in an approved file. Reject ambiguous edits.")]
    public async Task<ToolResult> EditAsync([Description("Relative or absolute file path; external paths require directory approval")] string path,
        [Description("Exact nonempty text to replace")] string old_string, [Description("Replacement text")] string new_string, CancellationToken ct = default)
    {
        var target = workspace.Resolve(path);
        if (new FileInfo(target).Length > 1000000) return ToolResult.Failure("File exceeds the edit size limit.");
        if (string.IsNullOrEmpty(old_string)) return ToolResult.Failure("old_string must be nonempty.");
        var content = await File.ReadAllTextAsync(target, ct);
        var index = content.IndexOf(old_string, StringComparison.Ordinal);
        if (index < 0 || content.IndexOf(old_string, index + old_string.Length, StringComparison.Ordinal) >= 0)
            return ToolResult.Failure("Expected exactly one match; no edit applied.");
        var updated = content[..index] + new_string + content[(index + old_string.Length)..];
        return await WriteAsync(path, updated, true, ct);
    }

    [AgentTool("list_directory"), Description("List a bounded page of entries in the workspace or an approved external directory without following links. Use path='.' for the workspace root, not '/' or an empty path.")]
    public Task<ToolResult> ListAsync([Description("Relative or absolute directory path; external paths require directory approval; '.' is the workspace root")] string path = ".",
        [Description("Entries to skip")] int offset = 0, [Description("Maximum entries, 1–100")] int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (offset is < 0 or > 10000 || limit is < 1 or > 100) return Task.FromResult(ToolResult.Failure("Invalid offset or limit."));
        var entries = Directory.EnumerateFileSystemEntries(workspace.Resolve(path)).Skip(offset).Take(limit + 1).ToArray();
        return Task.FromResult(ToolResult.Success(new
        {
            path, nextOffset = offset + Math.Min(entries.Length, limit),
            entries = entries.Take(limit).Select(p => new { name = Path.GetFileName(p), directory = Directory.Exists(p), link = (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0 }).ToArray()
        }, entries.Length > limit));
    }

    [AgentTool("run_shell", Risk = ToolRisk.Execute), Description("Run an installed executable by name or absolute path with literal arguments through the PowerShell 7.6 host. Trusted aliases run directly; other installed executables require one-time approval. No command-string interpolation.")]
    public async Task<ToolResult> RunShellAsync([Description("Installed executable name on PATH or fully qualified executable path; untrusted programs require per-call approval")] string exe,
        [Description("Literal argument array; each item is passed separately")] string[] args,
        [Description("Relative or absolute working directory; external paths require directory approval")] string working_directory = ".",
        [Description("Timeout in seconds: 1–120 foreground, 1–86400 background. Set explicitly for long-lived servers.")] int timeout_seconds = 30, CancellationToken ct = default,
        [Description("Return a background task ID immediately; inspect with shell_status and cancel with cancel_shell. The process is stopped on timeout or host shutdown.")] bool background = false)
    {
        var rejected = ValidateInvocation("run_shell", JsonSerializer.SerializeToElement(new { exe, args, working_directory, timeout_seconds, background }));
        if (rejected != null) return rejected;
        if (!ShellExecutablePolicy.TryResolveForExecution(shellAllowlist, exe, environment.PowerShell, out var executable, out _)) return ToolResult.Failure("Executable is no longer available at the resolved path.");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exe = executable, args })));
        // Constant trusted PowerShell source; user arguments are decoded as data.
        var info = BoundedProcess.StartInfo(environment.PowerShell!, ["-NoLogo", "-NoProfile", "-NonInteractive", "-CommandWithArgs", RuntimeScripts.Read("run_shell.ps1"), payload], workspace.Resolve(working_directory));
        info.RedirectStandardInput = true;
        checkpoints?.MarkExternalAction();
        if (background)
        {
            if (jobs == null) return ToolResult.Failure("This host does not support background shell jobs.");
            var taskId = jobs.Start(exe + " " + string.Join(" ", args), AgentExecutionScope.ConversationId ?? Guid.Empty, "shell", (id, token) => RunBackgroundShell(info, timeout_seconds, (text, error) => jobs.AppendOutput(id, error ? "[stderr] " + text : text), token));
            return ToolResult.Success(new { taskId, background = true });
        }
        var result = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(timeout_seconds), ct);
        return new(result.ExitCode == 0, new { exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr }, result.ExitCode == 0 ? null : "Executable exited with an error.", result.Truncated);
    }

    [AgentTool("shell_status"), Description("Inspect an existing background shell task; this does not launch the command again. Wait briefly for completion instead of repeatedly polling or restarting run_shell.")]
    public async Task<ToolResult> ShellStatusAsync([Description("Background shell task ID returned by run_shell")] string id,
        [Description("Host waits for completion without model polling, up to 0–60 seconds. Defaults to 60. Use 0 only for an explicit progress check; unfinished output stays in the terminal, completed output is returned.")] int wait_seconds = 60, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (wait_seconds is < 0 or > 60) return ToolResult.Failure("wait_seconds must be between 0 and 60.");
        var job = jobs?.Get(id);
        if (job?.Kind != "shell") return ToolResult.Failure("Shell task not found.");
        if (wait_seconds > 0 && job.State is LanAgent.Core.Services.BackgroundTaskState.Running or LanAgent.Core.Services.BackgroundTaskState.Queued)
        {
            try { job = await jobs!.WaitAsync(id, ct).WaitAsync(TimeSpan.FromSeconds(wait_seconds), ct); }
            catch (TimeoutException) { job = jobs!.Get(id); }
        }
        // Live output belongs to the host UI. Repeated unfinished snapshots must not
        // resend the growing output and step history to the model.
        return ToolResult.Success(job.State is LanAgent.Core.Services.BackgroundTaskState.Running or LanAgent.Core.Services.BackgroundTaskState.Queued
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

    private static async IAsyncEnumerable<LanAgent.Core.Services.AgentEvent> RunBackgroundShell(System.Diagnostics.ProcessStartInfo info, int seconds, Action<string, bool> onOutput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var result = await BoundedProcess.RunAsync(info, TimeSpan.FromSeconds(seconds), ct, onOutput: onOutput);
        yield return new LanAgent.Core.Services.TokenDelta($"\n退出码 {result.ExitCode}" + (result.Truncated ? "\n[输出已截断]" : ""));
        if (result.ExitCode != 0) yield return new LanAgent.Core.Services.AgentError($"退出码 {result.ExitCode}");
        yield return new LanAgent.Core.Services.AgentDone(result.ExitCode == 0 ? "stop" : "error");
    }
}
