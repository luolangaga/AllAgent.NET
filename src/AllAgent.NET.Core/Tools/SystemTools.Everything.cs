using System.ComponentModel;

namespace AllAgent.NET.Core.Tools;

public partial class SystemTools
{
    [AgentTool("everything_search"), Description("Fast Windows file/folder NAME search using bundled portable Everything: automatically starts its own background indexer, no installation or administrator required. First search indexes the requested directory; later searches reuse it. Prefer for local documents or filenames across large directories. query is literal text or a wildcard such as *.pdf, not Everything operators. Does not search contents or respect .gitignore. Scope defaults to workspace; external directories require approval. Index may lag filesystem changes. Continue with nextOffset when hasMore=true; use file_search for code contents.")]
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
