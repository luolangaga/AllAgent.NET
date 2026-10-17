using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class CheckpointManager(WorkspaceCheckpoints checkpoints, DataStore store, TerminalUi ui)
{
    public async Task StageAsync(Conversation conversation, Guid message)
    {
        var selected = store.GetMessages(conversation.Id).Single(m => m.Id == message);
        var draft = await SessionTimeline.RestoreDraftAsync(selected);
        var range = checkpoints.RevertRange(store, conversation.Id, message, preview: true);
        var external = range.Any(c => c.ExternalActions);
        if (!await ManagementUi.Confirm(ui, $"撤回 {range.Count} 轮任务及文件工具修改？历史会保留，/redo 可恢复。" + (external ? " Shell/MCP 的外部效果会保留。" : ""))) return;
        checkpoints.RevertRange(store, conversation.Id, message);
        ui.Draft = draft;
        if (ui.Inbox != null) ui.Inbox.AutomaticDeliveryPaused = true;
        FullScreenTerminal.Current?.Session(conversation.Id, conversation.ModelId, reload: true);
        ManagementUi.Success(ui, "已暂存撤回；/redo 恢复全部，提交新任务后才丢弃被撤回的历史。");
    }
    public async Task ExecuteAsync(string command, Conversation conversation, string? id = null)
    {
        if (command is "/undo" or "/rewind")
        {
            var latest = store.GetVisibleMessages(conversation.Id).LastOrDefault(m => m.Role == "user" && !m.IsInternal)
                ?? throw new ArgumentException("没有可撤回的用户消息。");
            await StageAsync(conversation, latest.Id); return;
        }
        if (command == "/redo" && conversation.Revert != null)
        {
            checkpoints.ClearRevert(store, conversation.Id);
            if (ui.Inbox != null) ui.Inbox.AutomaticDeliveryPaused = false;
            FullScreenTerminal.Current?.Session(conversation.Id, conversation.ModelId, reload: true);
            ManagementUi.Success(ui, "撤回已取消，原始历史和文件已恢复。"); return;
        }
        var list = checkpoints.List(conversation.Id).Where(c => !conversation.InvalidatedCheckpoints.Contains(c.Id)).ToArray();
        if (command == "/checkpoints")
        {
            var table = new Table().AddColumn("检查点").AddColumn("任务").AddColumn("文件 / 状态");
            foreach (var c in list.Take(30)) table.AddRow(c.Id.ToString()[..8], Markup.Escape(c.Label), $"{c.Files.Count(f => f.Completed)} · {(c.Undone ? "已撤销" : "当前历史")}" + (c.ExternalActions ? " · 含外部操作" : ""));
            TerminalOutput.Write(table); TerminalOutput.Write(new Text("/diff 查看修改 · /undo 撤销上一轮 · /redo 重做\n仅还原文件工具记录；Shell/MCP 的外部效果无法还原。\n", new Style(Color.Grey))); return;
        }
        var redo = command == "/redo";
        var selected = id != null ? list.SingleOrDefault(c => c.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase)) : command == "/diff" ? list.FirstOrDefault() : redo ? list.LastOrDefault(c => c.Undone) : list.FirstOrDefault(c => !c.Undone);
        if (selected == null) throw new ArgumentException("没有可用检查点。");
        if (command == "/diff")
        {
            foreach (var file in selected.Files.Where(f => f.Completed))
            {
                var rows = new List<Spectre.Console.Rendering.IRenderable>();
                if (file.Before != null) foreach (var line in Encoding.UTF8.GetString(file.Before).Replace("\r\n", "\n").Split('\n').Take(12)) rows.Add(new Text("- " + line, new Style(Color.Red)).Crop());
                if (file.After != null) foreach (var line in Encoding.UTF8.GetString(file.After).Replace("\r\n", "\n").Split('\n').Take(12)) rows.Add(new Text("+ " + line, new Style(Color.Green)).Crop());
                TerminalOutput.Write(new Panel(new Rows(rows)).Header(Markup.Escape(file.Path + $" · {file.Before?.Length ?? 0} → {file.After?.Length ?? 0} bytes")));
            }
            if (selected.Files.Count == 0) TerminalOutput.Write(new Text("这一轮没有文件工具修改。\n"));
            return;
        }
        var expectedMessages = redo ? selected.BeforeMessages : selected.AfterMessages;
        if (JsonSerializer.Serialize(conversation.Messages) != JsonSerializer.Serialize(expectedMessages))
            throw new InvalidOperationException("当前对话已发生变化；请按顺序撤销/重做。分支会话和新任务会使旧重做记录失效。");
        var action = redo ? "重做" : "撤销";
        if (!await ManagementUi.Confirm(ui, $"{action}任务“{selected.Label}”的对话和 {selected.Files.Count(f => f.Completed)} 个文件修改？" + (selected.ExternalActions ? " Shell/MCP 的外部效果会保留。" : ""))) return;
        var restored = checkpoints.Restore(conversation.Id, selected.Id, redo);
        conversation.Messages = redo ? restored.AfterMessages : restored.BeforeMessages;
        store.UpdateConversation(conversation);
        FullScreenTerminal.Current?.Session(conversation.Id, conversation.ModelId, reload: true);
        ManagementUi.Success(ui, action + "完成。" + (selected.ExternalActions ? " Shell/MCP 的外部效果未还原。" : ""));
    }
}
