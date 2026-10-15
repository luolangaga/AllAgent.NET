using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanAgent.Core.Data;
using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace LanAgent.Tests;

public sealed class StoragePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void UnchangedSettingsAndConversationsDoNotWriteOrRotateBackup()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        var conversation = new Conversation { Title = "persisted" };
        using (var store = new DataStore(directory))
        {
            store.AddConversation(conversation); store.SetSetting("theme", "dark");
            var writes = store.PersistenceWrites;
            var backup = File.ReadAllBytes(Path.Combine(directory, "lanagent.json.bak"));
            for (var i = 0; i < 20; i++) { store.SetSetting("theme", "dark"); store.UpdateConversation(conversation); }
            Assert.Equal(writes, store.PersistenceWrites);
            Assert.Equal(backup, File.ReadAllBytes(Path.Combine(directory, "lanagent.json.bak")));
        }
        using var reloaded = new DataStore(directory);
        reloaded.UpdateConversation(reloaded.GetConversation(conversation.Id)!);
        Assert.Equal(0, reloaded.PersistenceWrites);
        Assert.Equal("dark", reloaded.GetSetting("theme"));
    }

    [Fact]
    public void ChangedDataIsImmediatelyDurableAndBackupRecoversPreviousSnapshot()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        using (var store = new DataStore(directory))
        {
            store.SetSetting("version", "first"); store.SetSetting("version", "second");
            using var concurrentReader = new DataStore(directory);
            Assert.Equal("second", concurrentReader.GetSetting("version"));
            Assert.Equal(2, store.PersistenceWrites);
        }
        File.WriteAllText(Path.Combine(directory, "lanagent.json"), "broken JSON");
        using var recovered = new DataStore(directory);
        Assert.Equal("first", recovered.GetSetting("version"));
        recovered.SetSetting("version", "third");
        using var reader = new DataStore(directory);
        Assert.Equal("third", reader.GetSetting("version"));
    }

    [Fact]
    public void DeletedOrModifiedSnapshotIsRepairedOnAnOtherwiseUnchangedSave()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        using var store = new DataStore(directory);
        var conversation = new Conversation(); store.AddConversation(conversation);
        var path = Path.Combine(directory, "lanagent.json");
        File.Delete(path); store.UpdateConversation(conversation);
        File.WriteAllText(path, "corrupt"); store.UpdateConversation(conversation);
        Assert.Equal(3, store.PersistenceWrites);
        using var reader = new DataStore(directory);
        Assert.NotNull(reader.GetConversation(conversation.Id));
    }

    [Fact]
    public async Task PluginDisplayReusesManifestAndRefreshesAfterInstallOrRemoval()
    {
        using var workspace = new TestWorkspace();
        await using var plugins = new PluginHost(workspace.Root);
        plugins.Save(new("test", "1", workspace.Root, false));
        var first = plugins.Installed;
        Assert.Same(first, plugins.Installed);
        plugins.Save(new("test", "2", workspace.Root, false));
        Assert.Equal("2", Assert.Single(plugins.Installed).Version);
        await plugins.RemoveAsync("test"); Assert.Empty(plugins.Installed);
    }

    [Fact]
    public void RepeatedExternalActionsDoNotRewriteCheckpoint()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "checkpoints");
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(workspace.Root), directory);
        checkpoints.Begin(Guid.NewGuid(), "test"); checkpoints.MarkExternalAction();
        var path = Assert.Single(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
        var sentinel = DateTime.UtcNow.AddMinutes(-5); File.SetLastWriteTimeUtc(path, sentinel);
        checkpoints.MarkExternalAction();
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void LargeSnapshotBenchmarkPreservesTheSameData()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        using var store = new DataStore(directory);
        var conversation = new Conversation { Messages = [new Message { Content = new string('x', 22_000_000) }] };
        store.AddConversation(conversation);
        var active = new Conversation { Title = "active", Messages = [new Message { Content = "small current conversation" }] };
        store.AddConversation(active);
        var path = Path.Combine(directory, "lanagent.json");
        var baseline = Path.Combine(workspace.Root, "legacy.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        active.Title = "changed";
        var allocation = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        var json = JsonSerializer.Serialize(new DataStore.StoreData { Conversations = [conversation, active] }, options);
        using (var stream = new FileStream(baseline, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream)) { writer.Write(json); writer.Flush(); stream.Flush(true); }
        Assert.NotNull(JsonSerializer.Deserialize<DataStore.StoreData>(File.ReadAllText(baseline), options));
        var legacyMs = timer.Elapsed.TotalMilliseconds; var legacyAllocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
        allocation = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        var bytesBefore = store.ConversationBytesWritten;
        store.UpdateConversation(active);
        var currentMs = timer.Elapsed.TotalMilliseconds; var currentAllocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
        var written = store.ConversationBytesWritten - bytesBefore + new FileInfo(path).Length;
        Assert.InRange(written, 1, 10_000);
        using var reader = new DataStore(directory);
        Assert.Equal("changed", reader.GetConversation(active.Id)!.Title);
        Assert.Equal(22_000_000, reader.GetMessages(conversation.Id).Single().Content.Length);
        output.WriteLine($"Small conversation update with 22 MB unrelated history, one sample: legacy {legacyMs:F1} ms / {legacyAllocation:N0} allocated bytes / {new FileInfo(baseline).Length:N0} bytes written; current {currentMs:F1} ms / {currentAllocation:N0} allocated bytes / {written:N0} bytes written");
    }
}
