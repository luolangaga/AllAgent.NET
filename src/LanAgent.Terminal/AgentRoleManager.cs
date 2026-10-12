using System.Text.Json;
using LanAgent.Core.Services;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class AgentRoleManager(AgentRoleCatalog roles, TerminalUi ui, Func<string, bool>? inUse = null)
{
    public async Task ExecuteAsync(IReadOnlyList<string> args)
    {
        switch (args.FirstOrDefault() ?? "list")
        {
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
            default: throw new ArgumentException("用法：/agents list|show 名称|import 路径.json|remove 名称。");
        }
    }
}
