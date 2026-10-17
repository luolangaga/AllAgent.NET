using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Runtime;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class SubAgentNotificationTests
{
    [Fact]
    public async Task ParentConsumesChildCompletionBeforeEndingItsTurn()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        await using var tasks = new BackgroundTasks(); var parent = new Conversation(); store.AddConversation(parent);
        var provider = new CompletionProvider(tasks, parent.Id);
        var options = workspace.Options with { BackgroundTasks = tasks };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        var events = new List<AgentEvent>();
        await foreach (var evt in new AgentEngine(store, provider, runtime.CodeAct, options).RunAsync(parent.Id, "Delegate research", new ApiConfig { DefaultModel = "fixture" })) events.Add(evt);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Messages, message => message.Content.Contains("Verified child finding"));
        Assert.Equal("stop", Assert.IsType<AgentDone>(events.Last()).FinishReason);
        Assert.Single(store.GetMessages(parent.Id), message => message.IsInternal && message.Content.Contains("Verified child finding"));
    }

    private sealed class CompletionProvider(BackgroundTasks tasks, Guid parent) : IAiProvider
    {
        public string ProviderId => "fixture";
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("fixture", "fixture")]);
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                async IAsyncEnumerable<AgentEvent> Child(string id, [EnumeratorCancellation] CancellationToken token)
                { await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new TokenDelta("Verified child finding"); yield return new AgentDone("stop"); }
                var id = tasks.Start("Research", Guid.NewGuid(), "agent", Child, parent);
                tasks.NotifyParentOnCompletion(id); await tasks.WaitAsync(id, ct);
            }
            yield return new StreamChunk(ChunkType.TextDelta, TextDelta: Requests.Count == 1 ? "Research is running." : "Integrated finding.");
            yield return new StreamChunk(ChunkType.Done, FinishReason: "stop");
        }
    }

    [Fact]
    public async Task NotificationSurvivesRestartAndReplaysOnlyOnceToItsParent()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); var other = new Conversation(); store.AddConversation(parent); store.AddConversation(other);
        string id;
        await using (var tasks = new BackgroundTasks(Path.Combine(workspace.Root, "jobs")))
        {
            async IAsyncEnumerable<AgentEvent> Run(string job, [EnumeratorCancellation] CancellationToken ct)
            { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new TokenDelta("Verified finding"); yield return new AgentDone("stop"); }
            id = tasks.Start("Research", Guid.NewGuid(), "agent", Run, parent.Id);
            await tasks.WaitAsync(id);
            // Observing after completion must not lose a fast child result.
            tasks.NotifyParentOnCompletion(id);
            Assert.Empty(tasks.PendingNotifications(other.Id));
        }
        await using var reopened = new BackgroundTasks(Path.Combine(workspace.Root, "jobs"));
        var message = Assert.Single(SubAgentNotifications.Deliver(store, reopened, parent.Id));
        Assert.True(message.IsInternal); Assert.Contains("Verified finding", message.Content);
        Assert.Equal(Guid.Parse(id), message.Id);
        Assert.Empty(SubAgentNotifications.Deliver(store, reopened, parent.Id));
        Assert.Empty(store.GetMessages(other.Id));
        Assert.Single(store.GetMessages(parent.Id));
    }

    [Fact]
    public async Task ExistingPersistedMessageMakesInterruptedAcknowledgementIdempotent()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var parent = new Conversation(); store.AddConversation(parent); await using var tasks = new BackgroundTasks();
        async IAsyncEnumerable<AgentEvent> Run(string job, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new AgentDone("error"); }
        var id = tasks.Start("Failed lookup", Guid.NewGuid(), "agent", Run, parent.Id);
        await tasks.WaitAsync(id); tasks.NotifyParentOnCompletion(id);
        store.AddMessage(parent.Id, new Message { Id = Guid.Parse(id), ConversationId = parent.Id, Role = "user", Content = "Already persisted result", IsInternal = true });
        Assert.Empty(SubAgentNotifications.Deliver(store, tasks, parent.Id));
        Assert.Single(store.GetMessages(parent.Id)); Assert.True(tasks.Get(id).NotificationDelivered);
    }

    [Fact]
    public async Task ForegroundCompletionIsNotAutomaticallyReplayed()
    {
        await using var tasks = new BackgroundTasks(); var parent = Guid.NewGuid();
        async IAsyncEnumerable<AgentEvent> Run(string job, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new AgentDone("stop"); }
        var id = tasks.Start("Foreground", Guid.NewGuid(), "agent", Run, parent); await tasks.WaitAsync(id);
        Assert.Empty(tasks.PendingNotifications(parent));
    }
}
