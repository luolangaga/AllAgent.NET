using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class SkillManager(SkillCatalog catalog, TerminalUi ui, TerminalArguments cli)
{
    public void List()
    {
        var skills = catalog.List();
        ManagementUi.Output(ui, new { skills = skills.Select(s => new { s.Name, s.Description, s.Scope, s.Enabled, s.Path, s.UserInvocable, s.ModelInvocable }), diagnostics = catalog.Diagnostics }, () =>
        {
            var table = new Table().Title("[bold]Skills · 指令技能[/]").AddColumn("名称 / 来源").AddColumn("说明").AddColumn("状态");
            foreach (var s in skills) table.AddRow(Markup.Escape("/" + s.Name + " · " + s.Scope), Markup.Escape(s.Description), s.Enabled ? "[green]启用[/]" : "[dim]禁用[/]");
            TerminalOutput.Write(table);
            TerminalOutput.Write(new Text("项目目录：" + catalog.ProjectDirectory + "\n用户目录：" + catalog.UserDirectory + "\n全局目录：\n" + string.Join('\n', catalog.GlobalDirectories) + "\n"));
            foreach (var error in catalog.Diagnostics) ui.Error(error);
            TerminalOutput.MarkupLine("[dim]/skills add · import 路径 · show 名称 · enable/disable 名称 · /技能名 任务[/]");
        });
    }
    public async Task ExecuteAsync(IReadOnlyList<string> words)
    {
        var action = words.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        if (action == "panel")
        {
            List();
            var choice = ManagementUi.Choose("技能管理", "返回", "商店", "创建", "导入", "查看", "启用", "禁用", "卸载", "导出");
            if (choice == "返回") return;
            if (choice == "商店") { await ToolStorePage.Skills(catalog, ui, []); return; }
            action = choice switch { "创建" => "add", "导入" => "import", "查看" => "show", "启用" => "enable", "禁用" => "disable", "卸载" => "remove", _ => "export" };
            if (action == "import") words = new[] { action, await ManagementUi.Text("SKILL.md 或技能目录路径"), ManagementUi.Choose("安装位置", "project", "user") };
            else if (action is not "add")
            {
                var skills = catalog.List();
                if (skills.Count == 0) throw new ArgumentException("暂无技能。");
                var name = ManagementUi.Choose("选择技能", skills.Select(s => s.Name).ToArray());
                words = action == "export" ? new[] { action, name, await ManagementUi.Text("新的导出目录") } : new[] { action, name };
            }
        }
        switch (action)
        {
            case "store": case "search": await ToolStorePage.Skills(catalog, ui, words.Skip(1).ToArray()); return;
            case "list": List(); return;
            case "add":
                var name = cli.Get("name"); var description = cli.Get("description"); var file = cli.Get("instructions-file");
                if (name == null || description == null || file == null)
                {
                    ManagementUi.Interactive(ui);
                    name = await ManagementUi.Text("技能名称（小写字母、数字和短横线）");
                    description = await ManagementUi.Text("技能用途描述");
                    file = await ManagementUi.Text("指令文本文件路径（UTF-8 Markdown）");
                }
                if (new FileInfo(file).Length > 64000) throw new ArgumentException("指令文件过大。");
                var created = catalog.Create(name, description, await File.ReadAllTextAsync(file), cli.Get("scope") ?? "project");
                ManagementUi.Success(ui, "技能已创建：" + created.Path); return;
            case "import":
                var imported = catalog.Import(ManagementUi.Require(words, 1, "路径"), words.ElementAtOrDefault(2) ?? cli.Get("scope") ?? "project");
                ManagementUi.Success(ui, "技能已导入：/" + imported.Name); return;
            case "show":
                var skill = catalog.Get(ManagementUi.Require(words, 1, "技能名称"));
                ManagementUi.Output(ui, skill, () => TerminalOutput.Write(new Panel(new Text(skill.Instructions)).Header(Markup.Escape(skill.Name + " · " + skill.Scope)).Border(BoxBorder.Rounded))); return;
            case "enable": case "disable":
                catalog.Enable(ManagementUi.Require(words, 1, "技能名称"), action == "enable"); ManagementUi.Success(ui, "技能状态已更新。"); return;
            case "remove":
                var remove = ManagementUi.Require(words, 1, "技能名称");
                if (!await ManagementUi.Confirm(ui, "卸载 /" + remove + " 的 SKILL.md 入口？支持文件将保留。")) throw new OperationCanceledException();
                catalog.Remove(remove); ManagementUi.Success(ui, "技能入口已卸载。"); return;
            case "export":
                var exported = catalog.Get(ManagementUi.Require(words, 1, "技能名称"));
                Export(exported, ManagementUi.Require(words, 2, "新的导出目录")); ManagementUi.Success(ui, "技能包已导出。"); return;
            default: throw new ArgumentException("技能操作：list / add / import / show / enable / disable / remove / export。");
        }
    }
    private static void Export(InstructionSkill skill, string target)
    {
        target = Path.GetFullPath(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("导出目录已存在。");
        if (skill.Scope == "builtin")
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: " + skill.Name + "\ndescription: " + skill.Description + "\n---\n\n" + skill.Instructions);
            return;
        }
        var root = Path.GetDirectoryName(skill.Path)!;
        if (target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("导出目录不能位于技能包内部。");
        var policy = new WorkspacePolicy(root);
        var files = new List<string>();
        void Scan(string dir)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var safe = policy.Resolve(Path.GetRelativePath(root, path));
                if (Directory.Exists(safe)) Scan(safe); else files.Add(safe);
                if (files.Count > 500) throw new IOException("技能包文件过多。");
            }
        }
        Scan(root);
        Directory.CreateDirectory(target);
        var output = new WorkspacePolicy(target);
        foreach (var path in files)
        {
            var destination = output.Resolve(Path.GetRelativePath(root, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, false);
        }
    }
}
