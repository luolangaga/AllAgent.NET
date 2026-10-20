using System.ComponentModel;

namespace AllAgent.NET.Core.Tools;

public partial class SystemTools
{
    // Not registered as a model tool: file_search(names_only=true) covers name searches through
    // ripgrep. The bundled Everything engine stays in the package for hosts that prefer the
    // Windows-only index; restoring [AgentTool("everything_search")] plus a Description is the
    // only change needed to expose it again.
    public async Task<ToolResult> SearchEverythingAsync(
        [Description("Nonempty filename text or wildcard, e.g. invoice, *.pdf or *合同*.docx")] string query,
        [Description("Directory to search recursively; '.' is workspace. Use an explicit drive/directory for broader searches")] string path = ".",
        [Description("Positive page size; at most 2000 index entries are examined per call")] int limit = 50,
        [Description("Nonnegative index offset from the previous nextOffset")] int offset = 0,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (EverythingSearch.Validate(query, limit, offset) is { } error) return ToolResult.Failure(error);
        var root = workspace.Resolve(path);
        if (!Directory.Exists(root)) return ToolResult.Failure("Everything search path must be an existing directory.");
        if (!OperatingSystem.IsWindows()) return ToolResult.Failure("everything_search is available on Windows only. Use list_directory to browse directories.");
        return await EverythingSearch.SearchAsync(root, query, limit, offset, ct);
    }
}
