using System.ComponentModel;

namespace LanAgent.Core.Tools;

public sealed record AgentQuestion(string Question, IReadOnlyList<string> Choices, bool Multiple = false);

public partial class AgentInteractionTools(Func<AgentQuestion, CancellationToken, Task<string?>>? handler)
{
    public Task<ToolResult> AskAsync(string question, string[] choices, CancellationToken ct) => AskAsync(question, choices, false, ct);
    [AgentTool("ask_user"), Description("Ask the human for missing information or a meaningful choice. Supply up to 6 concise choices, or [] for free text. Set multiple=true when several choices can be selected; the answer is then a JSON array of the human's selected answers. Wait for their answer; never invent consent or an answer.")]
    public async Task<ToolResult> AskAsync([Description("A self-contained question, 1–500 characters")] string question,
        [Description("0–6 choices, each at most 120 characters; the human can always type another answer")] string[] choices,
        [Description("True when the human may select several choices; false for a single choice")] bool multiple = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 500 || choices.Length > 6 || choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 120))
            return ToolResult.Failure("Invalid user question or choices.");
        if (handler == null) return ToolResult.Failure("User interaction is unavailable in this host. Explain the missing information and stop.");
        var answer = await handler(new(question, choices, multiple), ct);
        return string.IsNullOrWhiteSpace(answer) ? ToolResult.Failure("The user declined to answer. Do not assume an answer or permission.") : ToolResult.Success(new { answer = ToolResult.Clip(answer) });
    }
}
