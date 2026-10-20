using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using HostMessage = AllAgent.NET.Core.Services.AI.ChatMessage;

namespace AllAgent.NET.Core.Services.AI;

internal sealed record ProviderStep(HostMessage Message, string FinishReason);

/// <summary>Adapter to the existing authenticated/routed IAiProvider pipeline. Native provider state
/// stays in the authoritative HostMessage and DataStore, not lossy SDK update metadata.</summary>
internal sealed class ProviderChatClient(Func<CancellationToken, Task<ProviderStep>> next) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<AiMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AiMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var step = await next(cancellationToken);
        var response = new ChatResponse(ToSdk(step.Message, modelOutput: true))
        {
            FinishReason = AgentEngine.IsOutputLimit(step.FinishReason) ? ChatFinishReason.Length :
                step.Message.ToolCallsJson != null ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
        };
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    internal static AiMessage ToSdk(HostMessage message, bool modelOutput = false)
    {
        var contents = new List<AIContent>();
        if (message.Content.Length > 0) contents.Add(new TextContent(message.Content));
        if (message.ReasoningContent is { Length: > 0 } reasoning) contents.Add(new TextReasoningContent(reasoning));
        foreach (var image in message.Images) contents.Add(new DataContent(Convert.FromBase64String(image.Base64), image.MediaType));
        if (message.ToolCallsJson is { } calls)
        {
            using var document = JsonDocument.Parse(calls);
            foreach (var call in document.RootElement.EnumerateArray())
            {
                var function = call.GetProperty("function");
                IDictionary<string, object?> arguments;
                try { arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(function.GetProperty("arguments").GetString()!) ?? []; }
                catch (JsonException) { arguments = new Dictionary<string, object?>(); } // The invocation uses the original payload and reports its parse failure.
                contents.Add(new FunctionCallContent(call.GetProperty("id").GetString()!, modelOutput ? "execute_code" : function.GetProperty("name").GetString()!, arguments));
            }
        }
        if (message.Role == "tool")
        {
            contents.Clear();
            contents.Add(new FunctionResultContent(message.ToolCallId ?? "", message.Content));
        }
        return new AiMessage(new ChatRole(message.Role), contents) { RawRepresentation = message };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
