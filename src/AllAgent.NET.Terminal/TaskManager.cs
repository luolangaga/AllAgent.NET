using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Data;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class TaskManager(BackgroundTasks tasks, TerminalUi ui, DataStore? store = null)
{
    private sealed record TaskAction(string Id, string Label);

    public void List()
    {
        ManagementUi.Output(ui, tasks.List, () =>
        {
            var table = new Table().Title(L.Pick("后台任务 / 子 Agent", "Background tasks / child agents")).AddColumn(L.Pick("ID / 任务", "ID / task")).AddColumn(L.Pick("状态", "State")).AddColumn(L.Pick("类型 / 会话", "Type / conversation"));
            foreach (var job in tasks.List.Take(30)) table.AddRow(Markup.Escape(job.Id[..8] + " · " + job.Title), State(job.State), Markup.Escape(job.Kind + " · " + job.SessionId.ToString()[..8]));
            TerminalOutput.Write(table); TerminalOutput.Write(new Text(L.Pick("直接聊天让 AI 派发子代理 · /background 任务 · /tasks show ID · approve ID · answer ID · cancel ID · /resume 会话ID\n", "Chat normally to let the AI dispatch child agents · /background task · /tasks show ID · approve ID · answer ID · cancel ID · /resume conversation-ID\n")));
        });
    }
    private static string State(BackgroundTaskState state) => state switch
    {
        BackgroundTaskState.Queued => L.Pick("排队", "Queued"),
        BackgroundTaskState.Running => L.Pick("运行中", "Running"),
        BackgroundTaskState.Cancelling => L.Pick("正在停止", "Stopping"),
        BackgroundTaskState.AwaitingApproval => L.Pick("[yellow]等待授权[/]", "[yellow]Awaiting approval[/]"),
        BackgroundTaskState.AwaitingAnswer => L.Pick("[yellow]等待回答[/]", "[yellow]Awaiting answer[/]"),
        BackgroundTaskState.Completed => L.Pick("[green]完成[/]", "[green]Completed[/]"),
        BackgroundTaskState.Cancelled => L.Pick("已取消", "Cancelled"),
        BackgroundTaskState.Interrupted => L.Pick("退出中断", "Interrupted by exit"),
        _ => L.Pick("[red]失败[/]", "[red]Failed[/]")
    };
    public async Task ExecuteAsync(IReadOnlyList<string> words, CancellationToken ct = default)
    {
        var action = words.FirstOrDefault() ?? (ui.AgentIsLive ? "list" : ui.Interactive && !ui.Json ? "panel" : "list");
        if (ui.AgentIsLive && action is "panel" or "wait") throw new ArgumentException(L.Pick("运行中请用 /tasks list|show|approve|answer|deny|cancel ID；任务持续执行，完成后可使用 wait。", "While running use /tasks list|show|approve|answer|deny|cancel ID; tasks keep running and wait is available after they finish."));
        if (action == "list") { List(); return; }
        if (action == "panel")
        {
            List(); if (tasks.List.Count == 0) return;
            var job = ManagementUi.Choose(L.Pick("选择任务（Esc 返回）", "Select a task (Esc to go back)"), tasks.List, j => j.Id[..8] + " · " + j.Title + " · " + j.State);
            var choice = ManagementUi.Choose(L.Pick("任务操作", "Task actions"), new[]
            {
                new TaskAction("show", L.Pick("查看结果", "Show result")),
                new TaskAction("interact", L.Pick("处理授权 / 回答", "Handle approval / answer")),
                new TaskAction("cancel", L.Pick("取消任务", "Cancel task")),
                new TaskAction("back", L.Pick("返回", "Back")),
            }, item => item.Label);
            if (choice.Id == "back") return;
            action = choice.Id == "interact" ? (tasks.Interaction(job.Id)?.Approval != null ? "approve" : "answer") : choice.Id;
            words = new[] { action, job.Id };
        }
        var id = ManagementUi.Require(words, 1, L.Pick("任务 ID", "task ID"));
        switch (action)
        {
            case "show":
                var info = tasks.Get(id);
                ManagementUi.Output(ui, info, () => TerminalOutput.Write(store != null && store.GetConversation(info.SessionId)?.ParentConversationId != null
                    ? new TaskTranscriptView(store, tasks, id, () => ui.ShowThinking)
                    : new Panel(new Text(Partial(info) + info.Output + (info.Error == null ? "" : L.Pick("\n错误：", "\nError: ") + info.Error))).Header(Markup.Escape(info.Id[..8] + " · " + info.State))));
                break;
            case "cancel":
                tasks.Cancel(id);
                if (ui.AgentIsLive) { ManagementUi.Success(ui, L.Pick("已请求停止任务 ", "Requested stop for task ") + id); break; }
                await ManagementUi.Busy(L.Pick("停止任务并等待执行器退出", "Stopping the task and waiting for the executor"), ct => tasks.WaitAsync(id, ct));
                ManagementUi.Success(ui, L.Pick("任务已停止 ", "Task stopped ") + id); break;
            case "wait":
                var result = await ManagementUi.Busy(L.Pick("等待后台任务 · Ctrl+C 返回面板", "Waiting for the background task · Ctrl+C returns to the panel"), async ct =>
                {
                    var waiting = tasks.WaitAsync(id, ct);
                    while (!waiting.IsCompleted)
                    {
                        if (tasks.Interaction(id) != null) return tasks.Get(id);
                        await Task.WhenAny(waiting, Task.Delay(80, ct)); ct.ThrowIfCancellationRequested();
                    }
                    return await waiting;
                });
                ManagementUi.Output(ui, result, () => TerminalOutput.Write(new Text(result.Output + "\n" + result.State + "\n")));
                if (result.State is BackgroundTaskState.AwaitingApproval or BackgroundTaskState.AwaitingAnswer) ManagementUi.Success(ui, L.Pick("任务需要你处理，请打开 /tasks。", "A task needs your attention; open /tasks.")); break;
            case "approve":
                var approval = tasks.Interaction(id)?.Approval ?? throw new ArgumentException(L.Pick("任务没有待处理授权。", "The task has no pending approval."));
                tasks.Respond(id, await ui.RequestApprovalAsync(approval with { Source = L.Pick("任务 ", "Task ") + id + (approval.Source == null ? "" : " · " + approval.Source) }, ct) ? "allow" : null); break;
            case "answer":
                var question = tasks.Interaction(id)?.Question ?? throw new ArgumentException(L.Pick("任务没有待回答的问题。", "The task has no pending question."));
                if (!ui.AgentIsLive) ManagementUi.Interactive(ui);
                tasks.Respond(id, await ui.RequestQuestionAsync(question with { Question = L.Pick("任务 ", "Task ") + id + L.Pick("：", ": ") + question.Question }, ct)); break;
            case "deny": tasks.Respond(id, null); break;
            default: throw new ArgumentException(L.Pick("任务操作：list / show / approve / answer / deny / cancel / wait。", "Task actions: list / show / approve / answer / deny / cancel / wait."));
        }
    }

    /// <summary>Cancelled, failed and interrupted children keep partial output only; never present it as a finished answer.</summary>
    internal static string Partial(BackgroundTaskInfo info) =>
        info.State != BackgroundTaskState.Completed && info.Output.Length > 0 ? L.Pick("（部分输出 · 任务未完成）\n", "(partial output · task not finished)\n") : "";
}
