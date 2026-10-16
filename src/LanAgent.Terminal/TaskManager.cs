using LanAgent.Core.Services;
using LanAgent.Core.Data;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class TaskManager(BackgroundTasks tasks, TerminalUi ui, DataStore? store = null)
{
    public void List()
    {
        ManagementUi.Output(ui, tasks.List, () =>
        {
            var table = new Table().Title("后台任务 / 子 Agent").AddColumn("ID / 任务").AddColumn("状态").AddColumn("类型 / 会话");
            foreach (var job in tasks.List.Take(30)) table.AddRow(Markup.Escape(job.Id[..8] + " · " + job.Title), State(job.State), Markup.Escape(job.Kind + " · " + job.SessionId.ToString()[..8]));
            TerminalOutput.Write(table); TerminalOutput.Write(new Text("直接聊天让 AI 派发子代理 · /background 任务 · /tasks show ID · approve ID · answer ID · cancel ID · /resume 会话ID\n"));
        });
    }
    private static string State(BackgroundTaskState state) => state switch { BackgroundTaskState.Queued => "排队", BackgroundTaskState.Running => "运行中", BackgroundTaskState.Cancelling => "正在停止", BackgroundTaskState.AwaitingApproval => "[yellow]等待授权[/]", BackgroundTaskState.AwaitingAnswer => "[yellow]等待回答[/]", BackgroundTaskState.Completed => "[green]完成[/]", BackgroundTaskState.Cancelled => "已取消", BackgroundTaskState.Interrupted => "退出中断", _ => "[red]失败[/]" };
    public async Task ExecuteAsync(IReadOnlyList<string> words, CancellationToken ct = default)
    {
        var action = words.FirstOrDefault() ?? (ui.AgentIsLive ? "list" : ui.Interactive && !ui.Json ? "panel" : "list");
        if (ui.AgentIsLive && action is "panel" or "wait") throw new ArgumentException("运行中请用 /tasks list|show|approve|answer|deny|cancel ID；任务持续执行，完成后可使用 wait。");
        if (action == "list") { List(); return; }
        if (action == "panel")
        {
            List(); if (tasks.List.Count == 0) return;
            var job = ManagementUi.Choose("选择任务（Esc 返回）", tasks.List, j => j.Id[..8] + " · " + j.Title + " · " + j.State);
            action = ManagementUi.Choose("任务操作", "查看结果", "处理授权 / 回答", "取消任务", "返回");
            if (action == "返回") return;
            action = action switch { "查看结果" => "show", "取消任务" => "cancel", _ => tasks.Interaction(job.Id)?.Approval != null ? "approve" : "answer" }; words = new[] { action, job.Id };
        }
        var id = ManagementUi.Require(words, 1, "任务 ID");
        switch (action)
        {
            case "show":
                var info = tasks.Get(id);
                ManagementUi.Output(ui, info, () => TerminalOutput.Write(store != null && store.GetConversation(info.SessionId)?.ParentConversationId != null
                    ? new TaskTranscriptView(store, tasks, id, () => ui.ShowThinking)
                    : new Panel(new Text(info.Output + (info.Error == null ? "" : "\n错误：" + info.Error))).Header(Markup.Escape(info.Id[..8] + " · " + info.State))));
                break;
            case "cancel":
                tasks.Cancel(id);
                if (ui.AgentIsLive) { ManagementUi.Success(ui, "已请求停止任务 " + id); break; }
                await ManagementUi.Busy("停止任务并等待执行器退出", ct => tasks.WaitAsync(id, ct));
                ManagementUi.Success(ui, "任务已停止 " + id); break;
            case "wait":
                var result = await ManagementUi.Busy("等待后台任务 · Ctrl+C 返回面板", async ct =>
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
                if (result.State is BackgroundTaskState.AwaitingApproval or BackgroundTaskState.AwaitingAnswer) ManagementUi.Success(ui, "任务需要你处理，请打开 /tasks。"); break;
            case "approve":
                var approval = tasks.Interaction(id)?.Approval ?? throw new ArgumentException("任务没有待处理授权。");
                tasks.Respond(id, await ui.RequestApprovalAsync(approval with { Source = "任务 " + id + (approval.Source == null ? "" : " · " + approval.Source) }, ct) ? "allow" : null); break;
            case "answer":
                var question = tasks.Interaction(id)?.Question ?? throw new ArgumentException("任务没有待回答的问题。");
                if (!ui.AgentIsLive) ManagementUi.Interactive(ui);
                tasks.Respond(id, await ui.RequestQuestionAsync(question with { Question = "任务 " + id + "：" + question.Question }, ct)); break;
            case "deny": tasks.Respond(id, null); break;
            default: throw new ArgumentException("任务操作：list / show / approve / answer / deny / cancel / wait。");
        }
    }
}
