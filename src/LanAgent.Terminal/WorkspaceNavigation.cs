using LanAgent.Core.Models;
using LanAgent.Core.Tools;

namespace LanAgent.Terminal;

internal static class WorkspaceNavigation
{
    public static string[] ParseCommand(string text)
    {
        var path = text["/workspace".Length..].Trim();
        if (path.Length == 0) return ["/workspace"];
        // The entire remainder is a folder path, not shell arguments. In particular,
        // a Windows trailing backslash must not escape the closing quote.
        if (path[0] is '\'' or '"')
        {
            if (path.Length < 2 || path[^1] != path[0]) throw new ArgumentException("文件夹路径的引号未闭合。");
            path = path[1..^1];
        }
        return ["/workspace", path];
    }

    public static string Resolve(string path, string current, bool fullAccess)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("用法：/workspace 文件夹路径。");
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? "" : path[2..]);
        var target = Path.GetFullPath(path, current);
        return new WorkspacePolicy(target, fullAccess: fullAccess).Root;
    }

    public static string[] Arguments(string[] original, TerminalArguments parsed, string target, bool fullAccess, Guid? resume = null)
    {
        var result = new List<string>();
        for (var i = 0; i < original.Length; i++)
        {
            var token = original[i];
            if (token == "--") { result.AddRange(original[i..]); break; }
            if (token is "--workspace" or "--resume" or "--config") { i++; continue; }
            if (token is "--ask" or "--full-access") continue;
            if (token == "--data-dir") { result.Add(token); result.Add(parsed.DataDirectory); i++; continue; }
            result.Add(token);
            if (token == "--allow-shell" || token.StartsWith("--") && parsed.Values.ContainsKey(token[2..]))
                result.Add(original[++i]);
        }
        // Put options before '--', when present, so they remain options on the next parse.
        var options = new List<string> { "--workspace", target, fullAccess ? "--full-access" : "--ask" };
        if (resume != null) { options.Add("--resume"); options.Add(resume.Value.ToString()); }
        var separator = result.IndexOf("--");
        result.InsertRange(separator < 0 ? result.Count : separator, options);
        return result.ToArray();
    }
}

/// <summary>Unwinds all workspace-owned resources before the same terminal enters a new workspace.</summary>
internal sealed class WorkspaceChange(string[] arguments, string[] previousArguments, ApiConfig? config, bool planMode) : Exception
{
    public string[] Arguments { get; } = arguments;
    public string[] PreviousArguments { get; } = previousArguments;
    public ApiConfig? Config { get; } = config == null ? null : ManagementUi.Clone(config);
    public bool PlanMode { get; } = planMode;
}
