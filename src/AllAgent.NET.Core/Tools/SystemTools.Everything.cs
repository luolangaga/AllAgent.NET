using System.ComponentModel;

namespace AllAgent.NET.Core.Tools;

public partial class SystemTools
{
    [AgentTool("everything_search"), Description("Fast Windows file/folder NAME search using the bundled portable Everything indexer (no install, no admin). query is literal text or a wildcard such as *.pdf, not Everything operators. Does not read contents or respect .gitignore; use file_search for code. Path defaults to the workspace; external directories need approval. Page with nextOffset when hasMore=true.")]
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
