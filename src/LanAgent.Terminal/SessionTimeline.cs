using LanAgent.Core.Data;
using LanAgent.Core.Models;

namespace LanAgent.Terminal;

internal static class SessionTimeline
{
    internal sealed record ForkChoice(Guid? MessageId, string Label);
    public static IReadOnlyList<Message> Entries(DataStore store, Guid session) => store.GetVisibleMessages(session)
        .Where(message => !message.IsInternal && message.Role == "user").Reverse().ToArray();
    public static Message Find(DataStore store, Guid session, string selector)
    {
        var matches = Entries(store, session).Where(message => message.Id.ToString().StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("用户消息 ID 不存在或不唯一。");
    }
    public static string Text(Message message) => message.InputText ?? message.Content;
    public static async Task<string> RestoreDraftAsync(Message message, CancellationToken ct = default)
    {
        var text = Text(message);
        if (message.Images.Count > 0)
        {
            foreach (var match in PromptReferences.ImageReferences(text).Reverse()) text = text.Remove(match.Index, match.Length);
            text = text.TrimEnd() + " " + await PastedImages.StageAsync(message.Images, ct);
        }
        if (text.Length > 32000) throw new ArgumentException("这条历史输入超过草稿长度上限，未恢复。");
        return text;
    }
}
