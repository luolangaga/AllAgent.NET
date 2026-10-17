using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Tools;

/// <summary>External MCP tools are provider-owned too, namespaced by server and approval-gated.</summary>
public sealed class McpToolSet : IAgentToolSet
{
    private readonly McpToolService _service;
    private readonly Dictionary<string, (string Server, string Tool)> _routes = new();
    public IReadOnlyList<AgentToolDescriptor> Definitions { get; }
    public McpToolSet(McpToolService service)
    {
        _service = service;
        var definitions = new List<AgentToolDescriptor>();
        foreach (var server in service.GetAllServerTools())
        {
            var prefix = "mcp_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.ServerId)))[..8].ToLowerInvariant();
            foreach (var tool in server.Tools)
            {
                var alias = prefix + "_" + tool.Name;
                _routes.Add(alias, (server.ServerId, tool.Name));
                definitions.Add(new(alias, tool.Description, JsonSerializer.Deserialize<JsonElement>(tool.InputSchemaJson), ToolRisk.Execute));
            }
        }
        Definitions = definitions;
    }
    public async ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        if (!_routes.TryGetValue(name, out var route)) return ToolResult.Failure("Unknown MCP tool.");
        return await _service.ExecuteServerToolAsync(route.Server, route.Tool, arguments, ct);
    }
}
