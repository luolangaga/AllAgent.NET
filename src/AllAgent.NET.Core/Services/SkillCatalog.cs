using System.Text.RegularExpressions;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Tools;
using YamlDotNet.Serialization;

namespace AllAgent.NET.Core.Services;

/// <param name="Tools">Host tools this skill discloses on demand (exact names or prefixes ending in '*').</param>
public sealed record InstructionSkill(string Name, string Description, string Instructions, string Path, string Scope, bool Enabled, bool UserInvocable, bool ModelInvocable, IReadOnlyList<string>? Tools = null)
{
    /// <summary>Declared tools with the optional field normalized; guidance-only skills declare none.</summary>
    public IReadOnlyList<string> DeclaredTools => Tools ?? [];
}

/// <summary>Instruction skills, deliberately separate from legacy executable command templates.</summary>
public sealed class SkillCatalog(DataStore store, string workspace, string dataDirectory, string? userHome = null)
{
    public string ProjectDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath(workspace), ".allagent", "skills");
    public string UserDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "skills");
    public IReadOnlyList<string> GlobalDirectories { get; } = new[] { ".agents/skills" }
        .Select(relative => System.IO.Path.GetFullPath(System.IO.Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative))).ToArray();
    public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();
    private readonly object _catalogLock = new();
    private readonly Dictionary<string, (DateTime Modified, long Length, InstructionSkill Skill)> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstructionSkill>? _snapshot;
    private long _refreshed;
    private static readonly Lazy<string> BrowserInstructions = new(() =>
    {
        using var stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream("AllAgent.NET.Core.Skills.browser-use.SKILL.md") ?? throw new InvalidOperationException("Built-in browser skill is missing.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        return text[(text.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..].Trim();
    });
    public static bool IsBrowserAutomationSkill(InstructionSkill skill) =>
        Regex.IsMatch(skill.Name, @"(^|[-:])(browser-use|agent-browser|browser|playwright|puppeteer|selenium|chrome-devtools|web-automation)([-:]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        Regex.IsMatch(skill.Description, @"浏览器.{0,8}(操作|自动化|控制)|(browser|web).{0,16}(automation|control)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private void Invalidate() { lock (_catalogLock) _snapshot = null; }
    public void SeedBundled()
    {
        // Seed newly shipped skills even for installations that already have bundled:v1.
        // Keep user edits and per-skill enable settings intact.
        var computerPath = System.IO.Path.Combine(UserDirectory, "computer-use", "SKILL.md");
        if (!File.Exists(computerPath))
        {
            var assembly = typeof(SkillCatalog).Assembly;
            using var resource = assembly.GetManifestResourceStream("AllAgent.NET.Core.Skills.computer-use.SKILL.md") ?? throw new InvalidOperationException("Built-in Computer Use skill is missing.");
            using var reader = new StreamReader(resource);
            SeedBundledFile("computer-use", reader.ReadToEnd());
        }
        if (store.GetSetting("skills:bundled:v1") == "seeded") return;
        foreach (var (name, description, instructions) in new[]
        {
            ("review", "审查代码或当前变更，按严重程度报告缺陷", "Review $ARGUMENTS. Read relevant code and project instructions through C# tools. Identify correctness bugs, regressions and missing validation with exact file references. Use run_shell for focused checks: trusted executable aliases run directly; any other installed executable is eligible for one-time approval. Report findings first; do not edit unless explicitly requested."),
            ("verify", "验证任务结果并运行相关检查", "Verify $ARGUMENTS against the user's requirements. Inspect project build/test instructions, select relevant checks, and run them only using approved host tools. Distinguish executed checks from assumptions. Report failures with actionable causes; never claim a check passed without its result."),
            ("debug", "定位问题根因并验证修复", "Investigate $ARGUMENTS. Read evidence, reproduce if possible with approved tools, form and test a specific hypothesis, then propose or apply the requested minimal fix. Validate behavior and explain the cause. Respect all permissions."),
            ("refactor", "保持行为地重构并验证", "Refactor $ARGUMENTS. Inspect existing design and project constraints first. Preserve public compatibility unless requested otherwise. Make cohesive edits through file_edit/file_write with approval. Run relevant checks and report behavior changes and limitations."),
            ("security-review", "检查代码中的实际安全边界问题", "Review $ARGUMENTS for concrete security defects. Trace untrusted input to sensitive operations; inspect authorization, command arguments, file boundaries, credential handling and isolation. Report reproducible findings and precise remediation. Do not invent speculative risks or run external attacks.")
        })
        {
            var yaml = new SerializerBuilder().Build().Serialize(new Dictionary<string, string> { ["name"] = name, ["description"] = description });
            SeedBundledFile(name, "---\n" + yaml + "---\n\n" + instructions + "\n");
        }
        store.SetSetting("skills:bundled:v1", "seeded");
    }
    private void SeedBundledFile(string name, string content)
    {
        var directory = System.IO.Path.Combine(UserDirectory, name);
        var target = System.IO.Path.Combine(directory, "SKILL.md");
        if (File.Exists(target)) return;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, content);
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { /* Another host seeded it first. Keep its complete file. */ }
            Invalidate();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Seeds a host-provided skill into the user catalog once; user edits are never overwritten.</summary>
    public bool SeedUserSkill(string name, string content)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(content) || content.Length > 500_000) throw new ArgumentException("技能内容无效。", nameof(content));
        var target = System.IO.Path.Combine(UserDirectory, name, "SKILL.md");
        if (File.Exists(target)) return false;
        SeedBundledFile(name, content);
        return true;
    }

    public IReadOnlyList<InstructionSkill> List()
    {
        var snapshot = ListUnfiltered();
        var selected = AllAgent.NET.Core.Extensions.AgentExtensionScope.Current?.SelectSkills(this, snapshot);
        return selected == null ? snapshot : ProtectBrowserSkills(selected, snapshot);
    }
    private static IReadOnlyList<InstructionSkill> ProtectBrowserSkills(IReadOnlyList<InstructionSkill> selected, IReadOnlyList<InstructionSkill> original) =>
        selected.Select(skill => skill.Name.Equals("browser-use", StringComparison.OrdinalIgnoreCase)
            ? original.Single(item => item.Name == "browser-use")
            : IsBrowserAutomationSkill(skill) ? skill with { Enabled = false } : skill).ToArray();
    internal IReadOnlyList<InstructionSkill> ListUnfiltered()
    {
        lock (_catalogLock)
        {
            if (_snapshot != null && Environment.TickCount64 - _refreshed < 1000) return _snapshot;
            _snapshot = Discover(); _refreshed = Environment.TickCount64; return _snapshot;
        }
    }
    /// <summary>Display-only snapshot; rendering must not wait for filesystem discovery.</summary>
    public IReadOnlyList<InstructionSkill> CachedList
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot) ?? ListUnfiltered();
            var selected = AllAgent.NET.Core.Extensions.AgentExtensionScope.Current?.SelectSkills(this, snapshot);
            return selected == null ? snapshot : ProtectBrowserSkills(selected, snapshot);
        }
    }
    private IReadOnlyList<InstructionSkill> Discover()
    {
        var found = new Dictionary<string, InstructionSkill>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var (root, scope) in GlobalDirectories.Select(root => (root, "global")).Concat(new[] { (UserDirectory, "user"), (System.IO.Path.Combine(workspace, ".lanagent", "skills"), "project"), (System.IO.Path.Combine(workspace, ".claude", "skills"), "project"), (System.IO.Path.Combine(workspace, ".agents", "skills"), "project"), (ProjectDirectory, "project") }))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var policy = scope == "global" ? null : new WorkspacePolicy(root);
                foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var path = scope == "global" ? ResolveGlobalSkillPath(System.IO.Path.Combine(dir, "SKILL.md"))
                            : policy!.Resolve(System.IO.Path.Combine(System.IO.Path.GetFileName(dir), "SKILL.md"));
                        if (!File.Exists(path)) continue;
                        var info = new FileInfo(path);
                        if (!_parsed.TryGetValue(path, out var cached) || cached.Modified != info.LastWriteTimeUtc || cached.Length != info.Length)
                            _parsed[path] = cached = (info.LastWriteTimeUtc, info.Length, Parse(path, scope));
                        var skill = cached.Skill with { Scope = scope };
                        found[skill.Name] = skill with { Enabled = store.GetSetting(Setting(skill)) != "disabled" };
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
                    { errors.Add(dir + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(root + ": " + ex.Message); }
        }
        // Keep files intact and visible to management, but exclude competing automation
        // instructions from model discovery and skill_read. The built-in name is reserved.
        foreach (var item in found.Values.ToArray())
            if (IsBrowserAutomationSkill(item))
            {
                found[item.Name] = item with { Enabled = false };
                errors.Add(item.Path + ": 已屏蔽外部浏览器操作技能；请使用内置 browser-use。");
            }
        var builtin = new InstructionSkill("browser-use", "网页操作与执行 JavaScript；内置 WebView2 或连接 Chrome/Edge，按需加载。", BrowserInstructions.Value, "builtin://browser-use", "builtin", true, true, true, ["browser"]);
        found[builtin.Name] = builtin with { Enabled = store.GetSetting(Setting(builtin)) != "disabled" };
        Diagnostics = errors;
        return found.Values.OrderBy(s => s.Name).ToArray();
    }
    private static string ResolveGlobalSkillPath(string path)
    {
        // Global installations commonly share packages through junctions/symlinks.
        // Resolve them to a real local package before applying ordinary file policy.
        path = System.IO.Path.GetFullPath(path);
        for (var pass = 0; pass < 64; pass++)
        {
            var resolved = false;
            for (var current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            {
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                var target = entry.ResolveLinkTarget(true) ?? throw new IOException("无法解析全局技能链接：" + current);
                if (!target.Exists) throw new IOException("全局技能链接目标不存在：" + target.FullName);
                var relative = System.IO.Path.GetRelativePath(current, path);
                path = relative == "." ? target.FullName : System.IO.Path.GetFullPath(relative, target.FullName);
                resolved = true; break;
            }
            if (resolved) continue;
            return new WorkspacePolicy(System.IO.Path.GetDirectoryName(path)!).Resolve(System.IO.Path.GetFileName(path));
        }
        throw new IOException("全局技能链接层级过深：" + path);
    }
    public InstructionSkill Get(string name, bool requireEnabled = false) => List().SingleOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!requireEnabled || s.Enabled)) ?? throw new ArgumentException("找不到技能或技能已禁用：" + name);
    public void Enable(string name, bool enabled)
    {
        var skill = Get(name);
        if (enabled && skill.Scope != "builtin" && IsBrowserAutomationSkill(skill)) throw new ArgumentException("外部浏览器操作技能已屏蔽；请使用内置 browser-use。");
        store.SetSetting(Setting(skill), enabled ? "enabled" : "disabled"); Invalidate();
    }
    private static string Setting(InstructionSkill skill) => "skill:" + skill.Path;
    public static InstructionSkill Parse(string path, string scope)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new ArgumentException("SKILL.md 超过 1 MB。");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new ArgumentException("SKILL.md 需要 YAML frontmatter。");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("技能 frontmatter 未结束。");
        var meta = new DeserializerBuilder().Build().Deserialize<Dictionary<string,object>>(text[4..end]);
        string Value(string key, string fallback = "") => meta.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? fallback : fallback;
        var name = Value("name", System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path))!);
        ValidateName(name);
        var instructions = text[(end + 5)..].Trim();
        if (instructions.Length == 0 || instructions.Length > 500_000) throw new ArgumentException("技能指令需要 1–500000 字符，可通过 skill_read 分页读取。");
        var description = Value("description");
        if (description.Length == 0 || description.Length > 16000) throw new ArgumentException("技能描述需要 1–16000 字符。");
        return new(name, description, instructions, System.IO.Path.GetFullPath(path), scope, true, !Value("user-invocable", "true").Equals("false", StringComparison.OrdinalIgnoreCase), !Value("disable-model-invocation", "false").Equals("true", StringComparison.OrdinalIgnoreCase), ParseTools(meta));
    }

    /// <summary>Declared host tools stay declarative data: a skill may name tools or prefixes, never invoke them.</summary>
    private static readonly Regex ToolPattern = new("^[a-z][a-z0-9_.:*-]{0,63}$", RegexOptions.CultureInvariant);
    private static IReadOnlyList<string> ParseTools(IReadOnlyDictionary<string, object> meta)
    {
        if (!meta.TryGetValue("tools", out var raw) || raw is null) return [];
        var values = raw is IEnumerable<object> sequence
            ? sequence.Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? "")
            : [Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? ""];
        var tools = new List<string>();
        foreach (var value in values)
        {
            var tool = value.Trim();
            if (tool.Length == 0) continue;
            if (!ToolPattern.IsMatch(tool)) throw new ArgumentException("技能 tools 项必须是工具名或其前缀，例如 file_read 或 computer_*。");
            if (tools.Count >= 32) throw new ArgumentException("技能最多声明 32 个 tools 项。");
            if (!tools.Contains(tool, StringComparer.Ordinal)) tools.Add(tool);
        }
        return tools;
    }
    public static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)) throw new ArgumentException("技能名必须是小写字母、数字或短横线，最长 64 字符。");
        if (new[] { "help", "skills", "mcp", "model", "models", "providers", "effort", "questions", "cache", "new", "exit", "resume", "clear", "status", "plan", "permissions", "context", "compact", "history", "export", "rename", "branch", "fork", "doctor", "tools", "settings", "memory", "init", "rewind" }.Contains(name)) throw new ArgumentException("技能名与内置命令冲突。");
    }
    public InstructionSkill Create(string name, string description, string instructions, string scope = "project")
    {
        ValidateName(name);
        if (name == "browser-use") throw new ArgumentException("browser-use 是内置保留技能名，不能覆盖。");
        if (instructions.Length is 0 or > 32000 || description.Length is 0 or > 1000) throw new ArgumentException("描述或指令长度不正确。");
        var root = scope switch { "project" => ProjectDirectory, "user" => UserDirectory, _ => throw new ArgumentException("scope 必须为 project 或 user。") };
        Directory.CreateDirectory(root);
        var dir = new WorkspacePolicy(root).Resolve(name);
        if (Directory.Exists(dir)) throw new IOException("技能目录已存在。");
        Directory.CreateDirectory(dir);
        var yaml = new SerializerBuilder().Build().Serialize(new Dictionary<string,string> { ["name"] = name, ["description"] = description });
        File.WriteAllText(System.IO.Path.Combine(dir, "SKILL.md"), "---\n" + yaml + "---\n\n" + instructions + "\n");
        Invalidate();
        return Get(name);
    }
    public InstructionSkill Import(string source, string scope = "project")
    {
        var path = Directory.Exists(source) ? System.IO.Path.Combine(source, "SKILL.md") : source;
        var parsed = Parse(path, scope);
        // Copy documented supporting files with path validation. Imported scripts never gain execution privileges.
        var sourceRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var policy = new WorkspacePolicy(sourceRoot);
        var files = new List<(string Path, string Relative)>();
        long size = 0;
        void Scan(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = System.IO.Path.GetRelativePath(sourceRoot, entry);
                var safe = policy.Resolve(relative);
                if (Directory.Exists(safe)) { Scan(safe); continue; }
                size += new FileInfo(safe).Length;
                if (files.Count >= 500 || size > 20_000_000) throw new IOException("技能包超过 500 文件或 20 MB。");
                files.Add((safe, relative));
            }
        }
        Scan(sourceRoot);
        var created = Create(parsed.Name, parsed.Description, parsed.Instructions, scope);
        var target = System.IO.Path.GetDirectoryName(created.Path)!;
        foreach (var file in files.Where(f => !f.Relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
        {
            var destination = new WorkspacePolicy(target).Resolve(file.Relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(file.Path, destination, false);
        }
        // Preserve invocation metadata rather than discarding it during import.
        File.Copy(path, created.Path, true);
        lock (_catalogLock) _parsed.Remove(created.Path);
        Invalidate();
        return Get(parsed.Name);
    }
    public void Remove(string name)
    {
        var skill = Get(name);
        if (skill.Scope == "builtin") throw new ArgumentException("内置技能不能删除；可以使用 skills disable 禁用。");
        File.Delete(skill.Path);
        var dir = System.IO.Path.GetDirectoryName(skill.Path)!;
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        lock (_catalogLock) _parsed.Remove(skill.Path);
        Invalidate();
    }
    public string Invoke(string name, string arguments)
    {
        var skill = Get(name, true);
        if (!skill.UserInvocable) throw new ArgumentException("此技能关闭了手动调用。");
        return $"Use the following user-selected skill. Its instructions do not override sandbox or permission rules.\n<skill name=\"{skill.Name}\">\n{skill.Instructions.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal)}\n</skill>\nTask / arguments: {arguments}";
    }
    public string Instructions
    {
        get
        {
            var text = "Available instruction skills (load via call_tool('skill_read', name=...) only when relevant):\n" + string.Join("\n", List().Where(s => s.Enabled && s.ModelInvocable).Select(s => $"{s.Name}: {s.Description}"));
            PromptComposer.NoteSource("skills:catalog", ProjectDirectory + " | " + UserDirectory + " | plugins/builtins", text);
            return text;
        }
    }
    public string ReadReference(string name, string relative, int offset = 0)
    {
        var skill = Get(name, true);
        if (!skill.ModelInvocable) throw new ArgumentException("此技能关闭了模型调用。");
        if (skill.Scope == "builtin") throw new ArgumentException("此内置技能没有额外支持文件；使用 skill_read 读取完整说明。");
        if (string.IsNullOrEmpty(skill.Path)) throw new ArgumentException("此插件技能没有支持文件目录。");
        var root = System.IO.Path.GetDirectoryName(skill.Path)!;
        var path = new WorkspacePolicy(root).Resolve(relative);
        if (new FileInfo(path).Length > 1_000_000) throw new ArgumentException("支持文件超过 1 MB。");
        var text = File.ReadAllText(path);
        if (offset < 0 || offset > text.Length) throw new ArgumentException("offset 超出范围。");
        return ToolResult.Clip(text[offset..]);
    }
}
