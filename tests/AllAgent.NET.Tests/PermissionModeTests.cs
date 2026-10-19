using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Restricted is the default host permission mode: workspace files and trusted shell aliases
/// run without a per-call prompt, everything outside the workspace keeps the host question.</summary>
public sealed class PermissionModeTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task RestrictedRunsWorkspaceFileOperationsWithoutAnApprover()
    {
        using var workspace = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { Permission = PermissionMode.Restricted });
        var write = await runtime.Tools.CallAsync("file_write", Args(new { path = "inside.txt", content = "受限模式" }), default);
        Assert.True(write.Ok, write.ToJson());
        Assert.Equal("受限模式", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "inside.txt")));
        var edit = await runtime.Tools.CallAsync("file_edit", Args(new { path = "inside.txt", edits = new[] { new { old_string = "受限", new_string = "限制" } } }), default);
        Assert.True(edit.Ok, edit.ToJson());
        Assert.Equal("限制模式", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "inside.txt")));
    }

    [Fact]
    public async Task RestrictedAsksOnceBeforeFileOperationsOutsideTheWorkspace()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var requests = new List<string>(); var allowed = false;
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Restricted,
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(allowed); }
        });
        var path = Path.Combine(outside.Root, "external.txt");
        var denied = await runtime.Tools.CallAsync("file_write", Args(new { path, content = "拒绝" }), default);
        Assert.False(denied.Ok); Assert.False(File.Exists(path)); Assert.Equal(["external_directory"], requests);
        allowed = true;
        var granted = await runtime.Tools.CallAsync("file_write", Args(new { path, content = "授权" }), default);
        Assert.True(granted.Ok, granted.ToJson()); Assert.Equal("授权", await File.ReadAllTextAsync(path));
        // The boundary question is the authorization; the write itself does not ask again.
        Assert.Equal(["external_directory", "external_directory"], requests);
    }

    [Fact]
    public async Task RestrictedDeniesExternalPathsWhenNoApproverIsAvailable()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { Permission = PermissionMode.Restricted });
        var path = Path.Combine(outside.Root, "external.txt");
        var denied = await runtime.Tools.CallAsync("file_write", Args(new { path, content = "x" }), default);
        Assert.False(denied.Ok); Assert.Contains("拒绝", denied.Error); Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RestrictedRunsTrustedShellAliasesAndAsksForUntrustedPrograms()
    {
        using var workspace = new TestWorkspace();
        var probe = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe");
        var requests = new List<string>();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Restricted,
            ShellAllowlist = new Dictionary<string, string> { ["probe"] = probe },
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(true); }
        });
        var trusted = await runtime.Tools.CallAsync("run_shell", Args(new { exe = "probe", args = new[] { "proof" } }), default);
        Assert.True(trusted.Ok, trusted.ToJson()); Assert.Empty(requests);
        // The host Python is installed but not an allowlisted alias: it keeps the per-call question.
        var untrusted = await runtime.Tools.CallAsync("run_shell", Args(new { exe = workspace.Python, args = new[] { "-c", "print('untrusted program')" } }), default);
        Assert.True(untrusted.Ok, untrusted.ToJson()); Assert.Equal(["run_shell"], requests);
    }

    [Fact]
    public async Task RestrictedKeepsArbitraryHostExecutionBehindApproval()
    {
        using var workspace = new TestWorkspace();
        var requests = new List<string>();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Restricted,
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(false); }
        });
        var denied = await runtime.Tools.CallAsync("powershell_script", Args(new { script = "Write-Output 'denied'" }), default);
        Assert.False(denied.Ok); Assert.Equal(["powershell_script"], requests);
    }

    [Fact]
    public async Task AskModeStillAsksForEveryWorkspaceWrite()
    {
        using var workspace = new TestWorkspace();
        var requests = new List<string>();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Ask,
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(false); }
        });
        var denied = await runtime.Tools.CallAsync("file_write", Args(new { path = "inside.txt", content = "x" }), default);
        Assert.False(denied.Ok); Assert.Equal(["file_write"], requests);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "inside.txt")));
    }

    /// <summary>A live permission source lets hosts switch the stance of a run that is already going:
    /// every tool call resolves the current mode instead of the one captured at runtime creation.</summary>
    [Fact]
    public async Task LivePermissionSwitchesApplyToTheNextToolCallOfARunningRuntime()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var mode = PermissionMode.Restricted;
        var requests = new List<string>();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Restricted,
            PermissionProvider = () => mode,
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(true); }
        });
        var path = Path.Combine(outside.Root, "external.txt");
        var asked = await runtime.Tools.CallAsync("file_write", Args(new { path, content = "restricted" }), default);
        Assert.True(asked.Ok, asked.ToJson()); Assert.Equal(["external_directory"], requests);

        // Full access answers every request itself, so the same runtime stops asking.
        mode = PermissionMode.Full;
        var unrestricted = Path.Combine(outside.Root, "external-full.txt");
        var granted = await runtime.Tools.CallAsync("file_write", Args(new { path = unrestricted, content = "full" }), default);
        Assert.True(granted.Ok, granted.ToJson()); Assert.Equal("full", await File.ReadAllTextAsync(unrestricted));
        Assert.Equal(["external_directory"], requests);

        // Ask puts every write back behind the approver, workspace files included.
        mode = PermissionMode.Ask;
        var framed = await runtime.Tools.CallAsync("file_write", Args(new { path = "inside.txt", content = "ask" }), default);
        Assert.True(framed.Ok, framed.ToJson()); Assert.Equal(["external_directory", "file_write"], requests);
    }

    /// <summary>Full access is not only an approver stance: the workspace boundary itself goes away
    /// for the running runtime, and comes back when the host narrows the mode again.</summary>
    [Fact]
    public async Task LiveFullAccessDropsTheWorkspaceBoundaryAndRestoresItOnNarrowing()
    {
        using var workspace = new TestWorkspace(); using var outside = new TestWorkspace();
        var note = Path.Combine(outside.Root, "note.txt");
        await File.WriteAllTextAsync(note, "external note");
        var mode = PermissionMode.Restricted;
        var requests = new List<string>();
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with
        {
            Permission = PermissionMode.Restricted,
            PermissionProvider = () => mode,
            Approver = (request, _) => { requests.Add(request.Name); return ValueTask.FromResult(false); }
        });
        var denied = await runtime.Tools.CallAsync("file_read", Args(new { path = note }), default);
        Assert.False(denied.Ok); Assert.Equal(["external_directory"], requests);

        mode = PermissionMode.Full;
        var read = await runtime.Tools.CallAsync("file_read", Args(new { path = note }), default);
        Assert.True(read.Ok, read.ToJson()); Assert.Contains("external note", read.ToJson());
        // No second question: full access never reaches the approver.
        Assert.Equal(["external_directory"], requests);

        mode = PermissionMode.Restricted;
        var narrow = await runtime.Tools.CallAsync("file_read", Args(new { path = note }), default);
        Assert.False(narrow.Ok); Assert.Equal(["external_directory", "external_directory"], requests);
    }
}
