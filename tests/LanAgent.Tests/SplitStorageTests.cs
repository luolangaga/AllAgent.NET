using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class SplitStorageTests
{
    [Fact]
    public void FailedManifestCommitDoesNotPublishPartialConversationChanges()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(workspace.Root);
        var conversation = new Conversation { Title = "before" }; store.AddConversation(conversation);
        using (var blocked = new FileStream(Path.Combine(workspace.Root, "lanagent.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            conversation.Title = "after";
            var failure = Record.Exception(() => store.UpdateConversation(conversation));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        using var reader = new DataStore(workspace.Root);
        Assert.Equal("before", reader.GetConversation(conversation.Id)!.Title);
    }

    [Fact]
    public void EmptyMainUsesBackupAndTwoBrokenManifestsCannotResetDatabase()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "lanagent.json");
        using (var store = new DataStore(workspace.Root)) store.SetSetting("kept", "value");
        File.WriteAllText(path, "");
        using (var reader = new DataStore(workspace.Root)) Assert.Equal("value", reader.GetSetting("kept"));
        File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "broken too");
        Assert.Throws<InvalidDataException>(() => new DataStore(workspace.Root));
        Assert.Equal("broken", File.ReadAllText(path));
    }

    [Fact]
    public void ActiveConversationAndSettingsDoNotRewriteOtherHistory()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "data");
        using var store = new DataStore(directory);
        var archived = new Conversation { Messages = [new Message { Content = new string('x', 2_000_000) }] };
        var active = new Conversation(); store.AddConversation(archived); store.AddConversation(active);
        var before = store.ConversationBytesWritten;
        store.AddMessage(active.Id, new Message { Content = "new message" });
        Assert.InRange(store.ConversationBytesWritten - before, 1, 5000);
        before = store.ConversationWrites;
        store.SetSetting("theme", "light"); Assert.Equal(before, store.ConversationWrites);
        Assert.True(new FileInfo(Path.Combine(directory, "lanagent.json")).Length < 5000);
        using var reader = new DataStore(directory);
        Assert.Equal(2_000_000, reader.GetMessages(archived.Id).Single().Content.Length);
        Assert.Equal("new message", reader.GetMessages(active.Id).Single().Content);
    }

    [Fact]
    public void LegacyDatabaseMigratesWithoutLosingBackupOrMessages()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "lanagent.json");
        var conversation = new Conversation { Messages = [new Message { Content = "legacy 中文" }] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DataStore.StoreData { Version = 2, Conversations = [conversation] });
        File.WriteAllBytes(path, bytes);
        using (var store = new DataStore(workspace.Root))
        {
            store.SetSetting("migration", "done");
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(workspace.Root, "lanagent.legacy-v2.json.bak")));
        }
        using var reader = new DataStore(workspace.Root);
        Assert.Equal("legacy 中文", reader.GetMessages(conversation.Id).Single().Content);
        Assert.Equal("done", reader.GetSetting("migration"));
    }

    [Fact]
    public void CorruptNewRevisionRecoversEntirePreviousCommit()
    {
        using var workspace = new TestWorkspace();
        var conversation = new Conversation { Title = "previous", Messages = [new Message { Content = "kept" }] };
        using (var store = new DataStore(workspace.Root))
        {
            store.AddConversation(conversation);
            conversation.Title = "latest"; store.UpdateConversation(conversation);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(workspace.Root, "lanagent.json")));
        var revision = manifest.RootElement.GetProperty("conversationFiles")[0].GetProperty("revision").GetString();
        File.WriteAllText(Path.Combine(workspace.Root, "conversations", $"{conversation.Id:N}-{revision}.json"), "broken");
        using var recovered = new DataStore(workspace.Root);
        Assert.Equal("previous", recovered.GetConversation(conversation.Id)!.Title);
        Assert.Equal("kept", recovered.GetMessages(conversation.Id).Single().Content);
    }

    [Fact]
    public void RevisionRetentionIsBoundedAndDeletionSurvivesRestart()
    {
        using var workspace = new TestWorkspace();
        var conversation = new Conversation();
        using (var store = new DataStore(workspace.Root))
        {
            store.AddConversation(conversation);
            for (var i = 0; i < 20; i++) { conversation.Title = i.ToString(); store.UpdateConversation(conversation); }
            Assert.InRange(Directory.GetFiles(Path.Combine(workspace.Root, "conversations"), "*.json").Length, 1, 2);
            store.DeleteConversation(conversation.Id); store.SetSetting("advance-backup", "1");
            Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Root, "conversations"), "*.json"));
        }
        using var reader = new DataStore(workspace.Root); Assert.Empty(reader.Conversations);
    }

    [Fact]
    public void ProjectImportKeepsSourceAndOnlyImportsMatchingSessionsOnce()
    {
        using var workspace = new TestWorkspace();
        var sourceDirectory = Path.Combine(workspace.Root, "global");
        var projectDirectory = Path.Combine(workspace.Root, ".lanagent", "data");
        using (var source = new DataStore(sourceDirectory))
        {
            source.AddConversation(new Conversation { Title = "current", WorkspaceRoot = workspace.Root });
            source.AddConversation(new Conversation { Title = "other", WorkspaceRoot = Path.Combine(workspace.Root, "other") });
            source.SetSetting("provider", "configured");
        }
        var sourceBytes = File.ReadAllBytes(Path.Combine(sourceDirectory, "lanagent.json"));
        var inFlight = Path.Combine(sourceDirectory, "lanagent.json.tmp");
        File.WriteAllText(inFlight, "writer owns this temporary file");
        using var project = new DataStore(projectDirectory);
        project.ImportProjectData(sourceDirectory, workspace.Root);
        Assert.Equal("current", Assert.Single(project.Conversations).Title);
        Assert.Equal("configured", project.GetSetting("provider"));
        project.SetSetting("provider", "local"); project.ImportProjectData(sourceDirectory, workspace.Root);
        Assert.Equal("local", project.GetSetting("provider"));
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(sourceDirectory, "lanagent.json")));
        Assert.Equal("writer owns this temporary file", File.ReadAllText(inFlight));
    }

    [Fact]
    public void ProjectIndexLivesInProgramDirectoryAndUnchangedRegistrationDoesNotWrite()
    {
        using var workspace = new TestWorkspace();
        var program = Path.Combine(workspace.Root, "program"); Directory.CreateDirectory(program);
        var a = Path.Combine(workspace.Root, "a"); var b = Path.Combine(workspace.Root, "b");
        ProjectDataDirectory.Register(a, Path.Combine(a, ".lanagent", "data"), program);
        ProjectDataDirectory.Register(b, Path.Combine(b, ".lanagent", "data"), program);
        var path = Path.Combine(program, "lanagent-projects.json");
        var sentinel = DateTime.UtcNow.AddMinutes(-5); File.SetLastWriteTimeUtc(path, sentinel);
        ProjectDataDirectory.Register(a, Path.Combine(a, ".lanagent", "data"), program);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(path));
        Assert.Equal(2, JsonSerializer.Deserialize<ProjectDataDirectory.ProjectEntry[]>(File.ReadAllBytes(path))!.Length);
        var args = TerminalArguments.Parse(["--workspace", a]);
        Assert.Equal(Path.Combine(a, ".lanagent", "data"), args.DataDirectory);
        var custom = TerminalArguments.Parse(["--workspace", a, "--data-dir", b]); Assert.Equal(b, custom.DataDirectory);
    }
}
