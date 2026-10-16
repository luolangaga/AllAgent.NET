using LanAgent.Core.Data;
using LanAgent.Core.Models;
using Spectre.Console;

namespace LanAgent.Terminal;

internal static class TodoCommand
{
    internal static string Line(TodoItem item) => (item.Status switch { "completed" => "☑ 已完成", "in_progress" => "▶ 进行中", _ => "☐ 待处理" }) + " · " + item.Content;

    internal static void Execute(DataStore store, TerminalUi ui, Guid id, string[] args)
    {
        var todos = store.GetTodos(id).ToList();
        var action = args.FirstOrDefault() ?? "list";
        if (action is "list" or "clear" && args.Length > 1 || action is "start" or "done" or "pending" or "remove" && args.Length != 2)
            throw new ArgumentException("用法 /todo [list|add 内容|start 序号|done 序号|pending 序号|remove 序号|clear]");
        if (action == "add") todos.Add(new(Guid.NewGuid().ToString("N"), string.Join(' ', args.Skip(1))));
        else if (action == "clear") todos.Clear();
        else if (action is "start" or "done" or "pending" or "remove")
        {
            if (!int.TryParse(args.ElementAtOrDefault(1), out var number) || number < 1 || number > todos.Count)
                throw new ArgumentException("请提供有效的 TODO 序号，使用 /todo 查看。");
            if (action == "remove") todos.RemoveAt(number - 1);
            else
            {
                if (action == "start") todos = todos.Select(t => t.Status == "in_progress" ? t with { Status = "pending" } : t).ToList();
                todos[number - 1] = todos[number - 1] with { Status = action == "start" ? "in_progress" : action == "done" ? "completed" : "pending" };
            }
        }
        else if (action != "list") throw new ArgumentException("用法 /todo [list|add 内容|start 序号|done 序号|pending 序号|remove 序号|clear]");
        if (action != "list") store.SetTodos(id, todos);
        ManagementUi.Output(ui, new { todos }, () => TerminalOutput.Write(new Text(
            "TODO · " + todos.Count(t => t.Status == "completed") + "/" + todos.Count + "\n" +
            (todos.Count == 0 ? "暂无待办 · /todo add 内容\n" : string.Join('\n', todos.Select((t, i) => $"{i + 1}. {TerminalSafeText.Clean(Line(t))}")) + "\n"))));
    }
}
