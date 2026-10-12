using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class InputInbox(DataStore store, string workspace)
{
    public Guid Session { get; set; }
    public bool SessionIsReverted => store.GetConversation(Session)?.Revert != null;
    private readonly HashSet<Guid> _paused = [];
    public bool AutomaticDeliveryPaused { get => _paused.Contains(Session); set { if (value) _paused.Add(Session); else _paused.Remove(Session); } }
    public IReadOnlyList<PendingInput> Pending => store.GetPendingInputs(Session);
    public PendingInput? Next => AutomaticDeliveryPaused ? null : Pending.FirstOrDefault(item => item.Delivery == "steer") ?? Pending.FirstOrDefault();
    public string Label => Pending.Count is var count && count > 0 ? $"待处理输入 {count}" + (AutomaticDeliveryPaused ? " · 已暂停" : "") + " · /queue" : "";
    public async Task<PendingInput> EnqueueAsync(string draft, string delivery = "queue", CancellationToken ct = default)
    {
        var expanded = await PromptReferences.ExpandWithImagesAsync(draft, workspace, ct, allowExplicitExternalImages: true);
        return store.EnqueueInput(Session, draft, expanded.Text, expanded.Images, delivery);
    }
    public async Task<string?> CommandAsync(string command, string draft, CancellationToken ct = default)
    {
        var words = CommandLineWords.Parse(command); var action = words.ElementAtOrDefault(1) ?? "list";
        if (words.Length == 1 && FullScreenTerminal.Current is { } screen)
            return await screen.PendingInputsAsync(this, draft, ct);
        if (action == "run" && words.Length == 2) { AutomaticDeliveryPaused = false; return null; }
        if (action == "list")
        {
            var table = new Table().AddColumn("ID").AddColumn("处理方式").AddColumn("输入");
            foreach (var item in Pending) table.AddRow(item.Id.ToString()[..8], item.Delivery == "steer" ? "当前任务" : "排队", Markup.Escape(item.Draft));
            TerminalOutput.Write(new Panel(table).Header("待处理输入 · /queue steer|queue|edit|delete ID")); return null;
        }
        if (words.Length != 3 || action is not ("steer" or "queue" or "edit" or "delete")) throw new ArgumentException("用法：/queue [list|steer|queue|edit|delete ID]");
        var matches = Pending.Where(item => item.Id.ToString().StartsWith(words[2], StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ArgumentException("输入 ID 不存在或不唯一。");
        var input = matches[0];
        if (action is "steer" or "queue") { store.UpdatePendingInput(Session, input.Id, action); return null; }
        if (action == "delete") { store.CancelPendingInput(Session, input.Id); return null; }
        // Prepare the whole replacement before removing the durable item; no lost attachments on failure.
        var restored = input.Draft;
        if (input.Images.Count > 0)
        {
            foreach (var match in PromptReferences.ImageReferences(restored).Reverse()) restored = restored.Remove(match.Index, match.Length);
            restored = restored.TrimEnd() + " " + await PastedImages.StageAsync(input.Images, ct);
        }
        var result = draft.Length == 0 ? restored : draft.TrimEnd() + "\n" + restored;
        if (result.Length > 32000) throw new ArgumentException("恢复后草稿超出 32000 字符；待处理输入已保留。");
        store.CancelPendingInput(Session, input.Id); return result;
    }
}
