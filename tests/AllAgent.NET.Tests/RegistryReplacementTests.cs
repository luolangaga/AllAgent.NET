using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class RegistryReplacementTests
{
    private class TestSet(params string[] names) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = names.Select(n => new AgentToolDescriptor(n, "test", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}"), ToolRisk.Read)).ToArray();
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => new(ToolResult.Success(new { name }));
    }
    private sealed class OtherSet(string name) : IAgentToolSet
    {
        private readonly TestSet _inner = new(name);
        public IReadOnlyList<AgentToolDescriptor> Definitions => _inner.Definitions;
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => _inner.InvokeAsync(name, arguments, ct);
    }
    [Fact]
    public void InvalidRegistrationOrReplacementKeepsExistingToolsIntact()
    {
        var registry = new ToolRegistry(); registry.Register(new TestSet("original")); registry.Register(new OtherSet("conflict"));
        Assert.Throws<InvalidOperationException>(() => registry.Replace<TestSet>(new TestSet("conflict")));
        Assert.Equal(new[] { "conflict", "original" }, registry.Definitions.Select(d => d.Name));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new TestSet("partial", "partial")));
        Assert.DoesNotContain(registry.Definitions, d => d.Name == "partial");
        registry.Replace<TestSet>(new TestSet("replacement")); Assert.Equal(new[] { "conflict", "replacement" }, registry.Definitions.Select(d => d.Name));
    }
}
