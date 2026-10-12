using System.ComponentModel;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

public partial class McpResourceTools(McpToolService service) : IAgentToolSet
{
    [AgentTool("mcp_resources", Risk = ToolRisk.Execute)]
    [Description("List text resources on a connected MCP server. Requires host approval.")]
    public async Task<ToolResult> ListAsync([Description("Connected MCP server ID")] string server_id, CancellationToken ct) => ToolResult.Success(await service.ListResourcesAsync(server_id, ct));
    [AgentTool("mcp_resource_read", Risk = ToolRisk.Execute)]
    [Description("Read a text resource from a connected MCP server. Requires host approval; result capped at 20000 characters.")]
    public Task<ToolResult> ReadAsync([Description("Connected MCP server ID")] string server_id, [Description("MCP resource URI")] string uri, CancellationToken ct) => service.ReadResourceAsync(server_id, uri, ct);
}
