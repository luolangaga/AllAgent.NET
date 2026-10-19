using System.ComponentModel;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Tools;

public partial class McpResourceTools(McpToolService service) : IAgentToolSet
{
    /// <summary>One entry point: omitting the URI lists resources, passing one reads it (both approval-gated).</summary>
    [AgentTool("mcp_resources", Risk = ToolRisk.Execute)]
    [Description("List or read text resources on a connected MCP server: omit uri to list, pass uri to read one resource (capped at 20000 characters). Requires host approval.")]
    public async Task<ToolResult> ResourcesAsync([Description("Connected MCP server ID")] string server_id,
        [Description("MCP resource URI; omit to list the server resources")] string uri = "", CancellationToken ct = default) =>
        string.IsNullOrEmpty(uri) ? ToolResult.Success(await service.ListResourcesAsync(server_id, ct)) : await service.ReadResourceAsync(server_id, uri, ct);
}
