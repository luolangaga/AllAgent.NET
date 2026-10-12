using System.Text.Json;
using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class ToolTests
{
    [Fact]
    public void GeneratorEmitsStrongContracts()
    {
        using var workspace = new TestWorkspace();
        var contracts = workspace.Tools.Definitions;
        Assert.Equal(7, contracts.Count);
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
        var second = await workspace.Tools.ReadAsync("large.txt", 25000);
        Assert.Equal("END", JsonSerializer.SerializeToElement(second.Data).GetProperty("content").GetString());
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
        Assert.False((await workspace.Tools.EditAsync("file.txt", "old", "new")).Ok);
        Assert.True((await workspace.Tools.WriteAsync("file.txt", "unique old", true)).Ok);
        Assert.True((await workspace.Tools.EditAsync("file.txt", "old", "new")).Ok);
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
        Assert.Equal(ToolResult.MaxOutputCharacters, json.GetProperty("stdout").GetString()!.Length);
        Assert.Equal(ToolResult.MaxOutputCharacters, json.GetProperty("stderr").GetString()!.Length);
    }

    [Fact]
    public async Task ShellTimeoutKillsProcessTree()
    {
        using var workspace = new TestWorkspace();
        await Assert.ThrowsAsync<TimeoutException>(() => workspace.Tools.RunShellAsync("probe", ["wait"], timeout_seconds: 1));
    }
}
