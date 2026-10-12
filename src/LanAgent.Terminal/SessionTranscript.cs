using System.Text.Json;
using LanAgent.Core.Models;

namespace LanAgent.Terminal;

internal static class SessionTranscript
{
    public static string LastReply(IEnumerable<Message> messages) => messages.LastOrDefault(m =>
        !m.IsInternal && m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))?.Content
        ?? throw new ArgumentException("此会话没有可复制的回复。");

    public static string Format(Conversation session, bool thinking = true, bool tools = true)
    {
        var sections = new List<string>();
        foreach (var message in session.Messages.TakeWhile(m => session.Revert == null || m.Id != session.Revert.MessageId).Where(m => !m.IsInternal).OrderBy(m => m.CreatedAt))
        {
            if (message.Role == "user") sections.Add("## User\n\n" + SessionTimeline.Text(message) +
                string.Concat(message.Images.Select(image => "\n\n![Attached image](" + image.DataUrl + ")")));
            else if (message.Role == "assistant")
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(message.Content)) parts.Add(message.Content);
                if (thinking && !string.IsNullOrEmpty(message.ReasoningContent)) parts.Add("_Thinking:_\n\n" + message.ReasoningContent);
                if (tools && message.ToolCallsJson != null)
                {
                    try
                    {
                        using var calls = JsonDocument.Parse(message.ToolCallsJson);
                        foreach (var call in calls.RootElement.EnumerateArray())
                        {
                            var function = call.GetProperty("function"); var id = call.GetProperty("id").GetString();
                            var input = function.GetProperty("arguments").GetString() ?? "";
                            var output = session.Messages.FirstOrDefault(m => m.Role == "tool" && m.ToolCallId == id)?.Content ?? "";
                            parts.Add("**Tool: " + function.GetProperty("name").GetString() + "**\n\n**Input:**\n" + Fence(input, "json") + "\n\n" + Fence(output, "json"));
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                    { parts.Add("**Stored tool call:**\n" + Fence(message.ToolCallsJson, "json")); }
                }
                if (parts.Count > 0) sections.Add("## Assistant\n\n" + string.Join("\n\n", parts));
            }
        }
        return $"# {session.Title}\n\n**Session ID:** {session.Id}\n**Created:** {session.CreatedAt:O}\n**Updated:** {session.UpdatedAt:O}\n\n---\n\n" + string.Join("\n\n---\n\n", sections) + "\n";
    }
    private static string Fence(string text, string language)
    {
        var run = 0; var longest = 0;
        foreach (var ch in text) { run = ch == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + language + "\n" + text + "\n" + fence;
    }
    public static async Task ExportAsync(Conversation session, string path, bool thinking = true, bool tools = true)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            await ManagementUi.WriteNewJsonAsync(path, session);
        else
        {
            await using var stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(Format(session, thinking, tools));
        }
    }
}
