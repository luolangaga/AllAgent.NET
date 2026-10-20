using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class TempPromptDump
{
    [Fact]
    public async Task Dump()
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new ComputerUseTools(new ComputerUseState()));
        registry.Register(new DumpSkill());
        await using var provider = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), new DumpSandbox());
        var before = provider.Instructions;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "prompt-before.txt"), before);
        await provider.CallToolAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "computer-use" }), default);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "prompt-after.txt"), provider.Instructions);
    }

    private sealed class DumpSkill : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } =
        [
            new("skill_read", "load skill", JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new Dictionary<string, object> { ["name"] = new { type = "string" }, ["offset"] = new { type = "integer" } }, required = new[] { "name" } }, ToolResult.JsonOptions), ToolRisk.Read)
        ];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => ValueTask.FromResult(ToolResult.Success(new { skill = new { name = "computer-use" }, content = "Use computer_* tools." }));
    }

    private sealed class DumpSandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
