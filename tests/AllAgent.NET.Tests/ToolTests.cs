using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ToolTests
{
    [Fact]
    public async Task ShellErrorsIdentifyTheFieldAndDoNotRunTheExecutable()
    {
        using var w = new TestWorkspace();
        var timeout = w.Tools.ValidateInvocation("run_shell", JsonSerializer.SerializeToElement(new { exe = "probe", args = Array.Empty<string>(), timeout_seconds = -1 }));
        Assert.Contains("timeout_seconds", timeout!.Error);
        var index = w.Tools.ValidateInvocation("run_shell", JsonSerializer.SerializeToElement(new { exe = "probe", args = new[] { "normal", "invalid\0argument" } }));
        Assert.Contains("args[1]", index!.Error);
        var registry = new ToolRegistry(); registry.Register(w.Tools);
        var invalid = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { command = "must not execute" }), default);
        Assert.False(invalid.Ok); Assert.Contains("run_shell", invalid.Error); Assert.Contains("exe:string", invalid.Error); Assert.Contains("args:array", invalid.Error);
    }
    [Fact]
    public async Task AmbiguousEditReportsLinesWithoutWriting()
    {
        using var workspace = new TestWorkspace();
        var content = "same\nother\nsame\n";
        var path = Path.Combine(workspace.Root, "ambiguous.txt");
        await File.WriteAllTextAsync(path, content);
        var result = await workspace.Tools.EditAsync("ambiguous.txt", [new("same", "changed")]);
        Assert.False(result.Ok);
        Assert.Contains("found 2", result.Error);
        Assert.Contains("1, 3", result.Error);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void GeneratorEmitsStrongContracts()
    {
        using var workspace = new TestWorkspace();
        var contracts = workspace.Tools.Definitions;
        Assert.Equal(new[] { "cancel_shell", "everything_search", "file_edit", "file_read", "file_search", "file_write", "list_directory", "run_shell", "shell_status" },
            contracts.Select(contract => contract.Name).Order(StringComparer.Ordinal));
        var shell = contracts.Single(t => t.Name == "run_shell");
        Assert.Equal(ToolRisk.Execute, shell.Risk);
        Assert.Equal("array", shell.Parameters.GetProperty("properties").GetProperty("args").GetProperty("type").GetString());
        Assert.False(shell.Parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "exe", "args" }, shell.Parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.All(contracts, t => Assert.NotEmpty(t.Description));
    }

    [Theory]
    [InlineData("{\"path\":1}")]
    [InlineData("{\"path\":\"x\",\"unexpected\":true}")]
    [InlineData("{}")]
    [InlineData("{\"path\":\"x\",\"offset\":\"1\"}")]
    public async Task InvalidArgumentsCannotReachTools(string json)
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        var result = await registry.CallAsync("file_read", JsonSerializer.Deserialize<JsonElement>(json), default);
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("data.txt:stream")]
    [InlineData("\\\\localhost\\share\\file")]
    [InlineData("NUL.txt")]
    [InlineData("sub/COM1")]
    [InlineData("sub/file.txt.")]
    public void WorkspaceRejectsEscapes(string path)
    {
        using var workspace = new TestWorkspace();
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspacePolicy(workspace.Root).Resolve(path));
    }

    [Fact]
    public async Task ExplicitMissingRuntimeCannotBeSilentlyReplaced()
    {
        using var workspace = new TestWorkspace();
        var environment = await new EnvironmentDoctor().CheckAsync(Path.Combine(workspace.Root, "missing-python.exe"), workspace.Pwsh);
        Assert.False(environment.Ready); Assert.Null(environment.Python);
    }

    [Fact]
    public async Task ReadPagesWithoutFloodingModel()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "large.txt"), new string('a', 25000) + "END");
        var result = await workspace.Tools.ReadAsync("large.txt");
        Assert.True(result.Ok); Assert.True(result.Truncated);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(ToolResult.MaxOutputCharacters, data.GetProperty("content").GetString()!.Length);
        Assert.Equal(1, data.GetProperty("totalLines").GetInt32());
        Assert.Equal(20000, data.GetProperty("nextColumnOffset").GetInt32());
        var second = await workspace.Tools.ReadAsync("large.txt", column_offset: 25000);
        Assert.Equal("END", JsonSerializer.SerializeToElement(second.Data).GetProperty("content").GetString());
    }

    [Fact]
    public async Task DirectoryFilteringIsOptionalAndPaginationIsStable()
    {
        using var workspace = new TestWorkspace();
        foreach (var name in new[] { "bin", "obj", ".git", "node_modules", "src" }) Directory.CreateDirectory(Path.Combine(workspace.Root, name));
        var result = await workspace.Tools.ListAsync();
        var entries = JsonSerializer.SerializeToElement(result.Data).GetProperty("entries");
        Assert.Single(entries.EnumerateArray()); Assert.Equal("src", entries[0].GetProperty("name").GetString());
        result = await workspace.Tools.ListAsync(include_ignored: true);
        Assert.Equal(5, JsonSerializer.SerializeToElement(result.Data).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task ShellAnsiFilteringHandlesSplitSequencesBeforeApplyingBudget()
    {
        var text = new string('x', 2047) + "\u001b[32mName\u001b[0m\u001b]0;title\a!";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        using var reader = new StreamReader(stream);
        var live = new System.Text.StringBuilder();
        var result = await BoundedProcess.DrainAsync(reader, 2052, default, s => live.Append(s), stripAnsi: true);
        Assert.Equal(new string('x', 2047) + "Name!", result.Text);
        Assert.Equal(result.Text, live.ToString()); Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ApprovalIsRequiredAndAuditDoesNotContainContents()
    {
        using var workspace = new TestWorkspace();
        var audit = Path.Combine(workspace.Root, "audit.jsonl");
        var denied = new ToolRegistry(auditPath: audit); denied.Register(workspace.Tools);
        var args = JsonSerializer.SerializeToElement(new { path = "write.txt", content = "SECRET_CONTENT" });
        Assert.False((await denied.CallAsync("file_write", args, default)).Ok);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "write.txt")));
        var approved = new ToolRegistry((_, _) => new(true), audit); approved.Register(workspace.Tools);
        Assert.True((await approved.CallAsync("file_write", args, default)).Ok);
        Assert.Equal("SECRET_CONTENT", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "write.txt")));
        var log = await File.ReadAllTextAsync(audit);
        Assert.DoesNotContain("SECRET_CONTENT", log); Assert.Contains("argumentsHash", log); Assert.Contains("started", log);
    }

    [Fact]
    public async Task EditsRejectAmbiguityAndOverwriteRequiresExplicitFlag()
    {
        using var workspace = new TestWorkspace();
        Assert.True((await workspace.Tools.WriteAsync("file.txt", "old old")).Ok);
        Assert.False((await workspace.Tools.WriteAsync("file.txt", "new")).Ok);
        Assert.False((await workspace.Tools.EditAsync("file.txt", [new("old", "new")])).Ok);
        Assert.True((await workspace.Tools.WriteAsync("file.txt", "unique old", true)).Ok);
        Assert.True((await workspace.Tools.EditAsync("file.txt", [new("old", "new")])).Ok);
        Assert.Equal("unique new", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "file.txt")));
    }

    [Fact]
    public async Task ShellUsesLiteralArgumentsAndTrustedPwsh()
    {
        using var workspace = new TestWorkspace();
        string[] args = ["space value", "a'b\"c", "$([IO.File]::WriteAllText('INJECTED','bad'))", "; exit 9", "中文"];
        var result = await workspace.Tools.RunShellAsync("probe", args);
        Assert.True(result.Ok, result.ToJson());
        var stdout = JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString()!;
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(stdout));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "INJECTED")));
        // Approval is enforced by the host registry, not by directly calling the tool implementation.
        var registry = new ToolRegistry(); registry.Register(workspace.Tools);
        var denied = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = "cmd", args = Array.Empty<string>() }), default);
        Assert.False(denied.Ok); Assert.Contains("approval denied", denied.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SimultaneousStdoutAndStderrAreDrainedAndClipped()
    {
        using var workspace = new TestWorkspace();
        var output = await workspace.Tools.RunShellAsync("probe", ["noisy"]);
        Assert.True(output.Ok, output.ToJson()); Assert.True(output.Truncated);
        var json = JsonSerializer.SerializeToElement(output.Data);
        // The window bounds JSON bytes; SDK output includes escaped line endings.
        Assert.Equal(ToolResult.MaxOutputCharacters, JsonEncodedText.Encode(json.GetProperty("stdout").GetString()!).EncodedUtf8Bytes.Length);
        Assert.Equal(ToolResult.MaxOutputCharacters, JsonEncodedText.Encode(json.GetProperty("stderr").GetString()!).EncodedUtf8Bytes.Length);
        var stdout = await File.ReadAllTextAsync(json.GetProperty("stdoutPath").GetString()!);
        var stderr = await File.ReadAllTextAsync(json.GetProperty("stderrPath").GetString()!);
        Assert.Equal(new string('o', 1_000_000), stdout.TrimEnd('\r', '\n'));
        Assert.Equal(new string('e', 1_000_000), stderr.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task ShellTimeoutKillsProcessTree()
    {
        using var workspace = new TestWorkspace();
        var result = await workspace.Tools.RunShellAsync("probe", ["wait"], timeout_seconds: 1);
        Assert.False(result.Ok); Assert.True(JsonSerializer.SerializeToElement(result.Data).GetProperty("timedOut").GetBoolean());
    }
}
