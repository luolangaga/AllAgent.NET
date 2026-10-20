using System.ComponentModel;
using System.Text.Json;

namespace AllAgent.NET.Core.Tools;

public sealed record AgentQuestion(string Question, IReadOnlyList<string> Choices, bool Multiple = false);

public partial class AgentInteractionTools(Func<AgentQuestion, CancellationToken, Task<string?>>? handler, TimeSpan? answerTimeout = null)
{
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;

    public Task<ToolResult> AskAsync(string question, string[] choices, CancellationToken ct) => AskAsync(question, choices, false, ct);
    [AgentTool("ask_user"), Description("Ask one self-contained question. Never mix independent questions in the choices. Use [] for free text; the human can always type an answer. With multiple=true, data.answer is an array, not a JSON string. The Other field accepts custom text directly in the same question. Wait for the answer; never invent consent.")]
    public async Task<ToolResult> AskAsync([Description("A self-contained question, 1–500 characters")] string question,
        [Description("0–6 choices, each at most 120 characters; the human can always type another answer")] string[] choices,
        [Description("True when the human may select several choices; false for a single choice")] bool multiple = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (AllAgent.NET.Core.Runtime.AgentExecutionScope.IsSubAgent)
            return ToolResult.Failure("Delegated agents cannot ask the user. Return the missing information or blocker to the parent agent in your final response; the parent decides whether to ask the user.");
        if (string.IsNullOrWhiteSpace(question) || question.Length > 500 || choices.Length > 6 || choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 120))
            return ToolResult.Failure("Invalid user question or choices.");
        if (handler == null) return ToolResult.Failure("User interaction is unavailable in this host. Explain the missing information and stop.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_answerTimeout);
        string? answer = null;
        var timedOut = false;
        try
        {
            answer = await handler(new(question, choices, multiple), timeout.Token).WaitAsync(_answerTimeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        finally
        {
            timedOut |= timeout.IsCancellationRequested && !ct.IsCancellationRequested;
            // WaitAsync can win the race against CancelAfter; cancelling explicitly makes sure the
            // host drops its pending prompt even though this method no longer waits for it.
            timeout.Cancel();
        }
        if (string.IsNullOrWhiteSpace(answer))
        {
            if (timedOut) return QuestionTimeout();
            return ToolResult.Failure("The user declined to answer. Do not assume an answer or permission.");
        }
        if (!multiple) return ToolResult.Success(new { answer = ToolResult.Clip(answer) });
        try
        {
            var answers = JsonSerializer.Deserialize<string[]>(answer);
            if (answers == null || answers.Any(a => string.IsNullOrWhiteSpace(a)))
                return ToolResult.Failure("The host returned an invalid multi-select answer.");
            return ToolResult.Success(new { answer = answers.Select(a => ToolResult.Clip(a)).ToArray() });
        }
        catch (JsonException) { return ToolResult.Failure("The host returned an invalid multi-select answer."); }
    }

    private ToolResult QuestionTimeout() =>
        ToolResult.Failure($"The user did not answer within {_answerTimeout.TotalSeconds:0.###} seconds; the question was skipped automatically. Continue without it and never assume an answer or permission.");
}
