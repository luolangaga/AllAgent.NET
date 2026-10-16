using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class InputInboxTests
{
    internal const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
    [Fact]
    public async Task PlainInputSteersAndExplicitQueueWaitsBehindIt()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var inbox = new InputInbox(store, workspace.Root) { Session = session.Id };
        await inbox.CommandAsync("/queue add next task", "");
        var guidance = await inbox.EnqueueAsync("correct current task");
        Assert.Equal("steer", guidance.Delivery);
        Assert.Equal(guidance.Id, inbox.Next!.Id);
        Assert.Equal("queue", inbox.Pending.Single(item => item.Id != guidance.Id).Delivery);
        Assert.Equal("next task", inbox.Pending.Single(item => item.Delivery == "queue").Draft);
    }

    [Fact]
    public void RestartPreservesImagesAndStepPromotionSeparatesQueuesFromSteers()
    {
        using var workspace = new TestWorkspace(); var directory = Path.Combine(workspace.Root, "data");
        Guid session, other, queue, steer;
        using (var store = new DataStore(directory))
        {
            var parent = new Conversation(); var foreign = new Conversation(); store.AddConversation(parent); store.AddConversation(foreign);
            session = parent.Id; other = foreign.Id;
            queue = store.EnqueueInput(session, "later", "queued content", []).Id;
            steer = store.EnqueueInput(session, "correction", "steered content", [ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String(Png))], "steer").Id;
            store.EnqueueInput(other, "foreign", "foreign", [], "steer");
        }
        using var reopened = new DataStore(directory);
        var promoted = Assert.Single(reopened.PromotePendingInputs(session, true));
        Assert.Equal(steer, promoted.Id); Assert.Equal(Png, Assert.Single(promoted.Images).Base64);
        Assert.Empty(reopened.PromotePendingInputs(session, true));
        Assert.Equal(queue, Assert.Single(reopened.GetPendingInputs(session)).Id);
        Assert.Equal(queue, Assert.Single(reopened.PromotePendingInputs(session, false)).Id);
        Assert.Single(reopened.GetPendingInputs(other)); Assert.Empty(reopened.GetMessages(other));
        Assert.Throws<InvalidOperationException>(() => reopened.CancelPendingInput(session, steer));
        Assert.Throws<InvalidOperationException>(() => reopened.UpdatePendingInput(session, steer, "queue"));
        Assert.Equal(2, reopened.GetMessages(session).Count);
    }
    [Fact]
    public async Task ConcurrentPromotionAndCancelCannotDuplicateOrLeakAcrossSessions()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); var other = new Conversation(); store.AddConversation(session); store.AddConversation(other);
        var items = Enumerable.Range(0, 12).Select(index => store.EnqueueInput(session.Id, "item " + index, "content " + index, [])).ToArray();
        Assert.Throws<InvalidOperationException>(() => store.CancelPendingInput(other.Id, items[0].Id));
        Assert.Throws<InvalidOperationException>(() => store.UpdatePendingInput(other.Id, items[0].Id, "steer"));
        store.CancelPendingInput(session.Id, items[3].Id);
        var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => Task.Run(() => store.PromotePendingInputs(session.Id, false))));
        Assert.Equal(11, results.Sum(messages => messages.Count));
        var messages = store.GetMessages(session.Id);
        Assert.Equal(items.Where(item => item.Id != items[3].Id).Select(item => item.Id), messages.Select(message => message.Id));
        Assert.Empty(store.GetPendingInputs(session.Id)); Assert.Empty(store.GetMessages(other.Id));
    }
    [Fact]
    public async Task EditRestoresSnapshotImagesAndKeepsExistingDraft()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session); var inbox = new InputInbox(store, workspace.Root) { Session = session.Id };
        var image = Path.Combine(workspace.Root, "input.png"); await File.WriteAllBytesAsync(image, Convert.FromBase64String(Png));
        var pending = await inbox.EnqueueAsync("Analyze @input.png"); File.Delete(image);
        var draft = await inbox.CommandAsync("/queue edit " + pending.Id.ToString()[..8], "existing draft");
        Assert.StartsWith("existing draft\nAnalyze", draft); Assert.Empty(inbox.Pending);
        var restored = await PromptReferences.ExpandWithImagesAsync(draft!, workspace.Root, allowExplicitExternalImages: true);
        Assert.Equal(Png, Assert.Single(restored.Images).Base64);
        var newItem = await inbox.EnqueueAsync("next");
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.CommandAsync("/queue edit " + newItem.Id, new string('x', 32000)));
        Assert.Single(inbox.Pending);
        await inbox.CommandAsync("/queue steer " + newItem.Id, ""); Assert.Equal("steer", Assert.Single(inbox.Pending).Delivery);
        inbox.AutomaticDeliveryPaused = true; Assert.Null(inbox.Next);
        await inbox.CommandAsync("/queue run", ""); Assert.Equal(newItem.Id, inbox.Next!.Id);
    }
}
