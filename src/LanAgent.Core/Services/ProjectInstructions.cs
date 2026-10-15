using LanAgent.Core.Tools;

namespace LanAgent.Core.Services;

/// <summary>Host-owned project context shared by terminal, desktop and embedded runtimes.</summary>
public static class ProjectInstructions
{
    public const int MaxCharacters = 100000;

    public static string Load(string workspace, bool includeRoot = true)
    {
        var policy = new WorkspacePolicy(workspace);
        var sections = new List<string>();
        foreach (var relative in includeRoot ? new[] { "AGENTS.md", ".lanagent/AGENT.md" } : new[] { ".lanagent/AGENT.md" })
        {
            var path = policy.Resolve(relative);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > MaxCharacters * 4L + 4)
                throw new ArgumentException(relative + " 超过项目指令大小上限（100000 字符）。");
            var content = File.ReadAllText(path);
            if (content.Length > MaxCharacters)
                throw new ArgumentException(relative + " 超过项目指令大小上限（100000 字符）。");
            sections.Add("Project instructions from " + relative + ":\n" + content);
        }
        return string.Join("\n\n", sections);
    }
}
