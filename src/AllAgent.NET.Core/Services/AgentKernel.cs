using System.Threading.Channels;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AllAgent.NET.Core.Services;

internal sealed record AgentKernelRun(ChannelWriter<AgentEvent> Writer, Guid ConversationId, string Text, ApiConfig Config,
    bool InternalMessage, IReadOnlyList<ImageAttachment> Images, bool FromInbox, string? InputText);

internal interface IAgentKernel
{
    Task RunAsync(AgentKernelRun request, CancellationToken ct);
}

internal sealed class LegacyAgentKernel(Func<AgentKernelRun, CancellationToken, Task> run) : IAgentKernel
{
    public Task RunAsync(AgentKernelRun request, CancellationToken ct) => run(request, ct);
}

internal sealed class FrameworkAgentKernel(Func<FrameworkAgentKernel, AgentKernelRun, CancellationToken, Task> run) : IAgentKernel
{
    public Task RunAsync(AgentKernelRun request, CancellationToken ct) => run(this, request, ct);

    internal async Task RunLoopAsync(ProviderChatClient provider, AIFunction execute,
        Func<IEnumerable<AiMessage>> history, Func<string> instructions, CancellationToken ct)
    {
        // FunctionInvokingChatClient owns normal model -> tool -> model iteration. There is no second
        // tool loop in AllAgent. The outer coordinator only resumes for steers, compaction and length.
        using var functions = new FunctionInvokingChatClient(provider)
        {
            AllowConcurrentInvocation = false,
            MaximumIterationsPerRequest = int.MaxValue,
            MaximumConsecutiveErrorsPerRequest = 0
        };
        var agent = new ChatClientAgent(functions, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatOptions = new() { Tools = [execute] },
            ChatHistoryProvider = new DataStoreChatHistoryProvider(history),
            AIContextProviders = [new CodeActContextProvider(instructions)]
        });
        var session = await agent.CreateSessionAsync(ct);
        await foreach (var _ in agent.RunStreamingAsync(Array.Empty<AiMessage>(), session, cancellationToken: ct)) { }
    }
}

/// <summary>DataStore is authoritative; the provider projection is never persisted a second time.</summary>
internal sealed class DataStoreChatHistoryProvider(Func<IEnumerable<AiMessage>> history) : ChatHistoryProvider
{
    protected override ValueTask<IEnumerable<AiMessage>> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(history());
    }
}

internal sealed class CodeActContextProvider(Func<string> instructions) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AIContext { Instructions = instructions() });
}
