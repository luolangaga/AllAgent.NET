using System.ComponentModel;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

public partial class PythonLibraryTools(PythonPackageEnvironment environment) : IAgentToolSet
{
    [AgentTool("python_library_docs")]
    [Description("Read an installed library's bundled usage documentation and official documentation links. Use this when unfamiliar with a library instead of inventing API calls. Built-in common libraries do not have extra documentation.")]
    public Task<ToolResult> ReadAsync([Description("Installed distribution name, e.g. requests")] string package, [Description("Character offset for paged documentation")] int offset = 0, CancellationToken ct = default)
    {
        var text = environment.ReadDocument(package);
        if (offset < 0 || offset > text.Length) return Task.FromResult(ToolResult.Failure("Invalid documentation offset."));
        var content = text.Substring(offset, Math.Min(ToolResult.MaxOutputCharacters, text.Length - offset));
        return Task.FromResult(ToolResult.Success(new { content, nextOffset = offset + content.Length, totalCharacters = text.Length }, offset + content.Length < text.Length));
    }
}
