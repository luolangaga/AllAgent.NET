using System.Text.Json;
using LanAgent.Core.Services;
using LanAgent.Core.Models;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class AgentRoleManager(AgentRoleCatalog roles, TerminalUi ui, Func<string, bool>? inUse = null,
    Func<IReadOnlyList<ApiConfig>>? configurations = null)
{
    public async Task ExecuteAsync(IReadOnlyList<string> args)
    {
        switch (args.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list"))
        {
            case "panel":
                ManagementUi.Interactive(ui);
                while (true)
                {
                    await ExecuteAsync(["list"]);
                    var action = ManagementUi.Choose("子代理配置 · ↑↓ 选择，Enter 确认", "返回聊天", "编辑角色", "新建角色", "恢复内置 / 删除自定义角色");
                    if (action == "返回聊天") return;
                    if (action == "新建角色") { await EditAsync(null); continue; }
                    var role = ManagementUi.Choose("选择角色", roles.List(), r => r.Name + " · " + r.Description);
                    if (action == "编辑角色") await EditAsync(role);
                    else if (await ManagementUi.Confirm(ui, "恢复内置或删除自定义角色 " + role.Name + "？")) await ExecuteAsync(["remove", role.Name]);
                }
            case "edit":
                ManagementUi.Interactive(ui);
                await EditAsync(args.Count > 1 ? roles.Get(args[1]) : ManagementUi.Choose("选择角色", roles.List(), r => r.Name + " · " + r.Description));
                break;
            case "add":
                ManagementUi.Interactive(ui); await EditAsync(null); break;
            case "list":
                ManagementUi.Output(ui, roles.List(), () =>
                {
                    var table = new Table().AddColumn("角色").AddColumn("说明").AddColumn("权限").AddColumn("默认模型 / 提供商");
                    foreach (var role in roles.List()) table.AddRow(Markup.Escape(role.Name), Markup.Escape(role.Description), role.ReadOnly ? "只读" : "继承父代理", Markup.Escape((role.Model ?? "继承") + " / " + (role.Provider ?? "继承")));
                    TerminalOutput.Write(table);
                }); break;
            case "show":
                var selected = roles.Get(ManagementUi.Require(args, 1, "角色名"));
                ManagementUi.Output(ui, selected, () => TerminalOutput.Write(new Text(JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true })))); break;
            case "import":
                var path = Path.GetFullPath(ManagementUi.Require(args, 1, "JSON 文件路径"), ui.Workspace);
                if (new FileInfo(path).Length > 2_000_000) throw new ArgumentException("Agent 角色文件过大。");
                roles.Import(await File.ReadAllTextAsync(path)); ManagementUi.Success(ui, "Agent 角色配置已保存。"); break;
            case "remove":
                var name = ManagementUi.Require(args, 1, "角色名");
                if (inUse?.Invoke(name) == true) throw new InvalidOperationException("此角色仍有运行中的任务；请等待完成或取消后再移除。");
                roles.Remove(name); ManagementUi.Success(ui, "自定义角色已移除；内置角色恢复默认配置。"); break;
            default: throw new ArgumentException("用法：/agents [panel|edit [名称]|add|list|show 名称|import 路径.json|remove 名称]。");
        }
    }

    private async Task EditAsync(AgentRoleDefinition? role)
    {
        if (role == null)
        {
            var name = (await ManagementUi.Text("新角色名称（小写字母、数字、_ 或 -）")).Trim();
            if (roles.List().Any(r => r.Name == name)) throw new ArgumentException("角色已存在，请选择编辑角色。");
            role = new(name, "自定义代理", "完成主代理交给你的任务，遵守宿主权限并返回结果。");
        }
        var editor = new AgentRoleEditor(configurations ?? (() => []), error: ui.Error, save: draft =>
        {
            if (inUse?.Invoke(draft.Name) == true) throw new InvalidOperationException("此角色仍有运行中的任务；请等待完成或取消后再保存。草稿尚未保存。");
            roles.Import(JsonSerializer.Serialize(new[] { draft }));
        });
        if (await editor.EditAsync(role) != null) ManagementUi.Success(ui, "子代理角色配置已保存，新任务生效。");
        else ManagementUi.Success(ui, "已取消，角色配置未修改。");
    }
}
