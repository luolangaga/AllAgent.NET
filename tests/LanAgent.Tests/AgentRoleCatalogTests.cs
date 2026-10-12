using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class AgentRoleCatalogTests
{
    [Fact]
    public async Task ManagerDoesNotDeleteRoleUsedByLiveTask()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var catalog = new AgentRoleCatalog(store);
        catalog.Import("[{\"name\":\"reviewer\",\"description\":\"Review\",\"instructions\":\"Review\",\"readOnly\":true}]");
        var manager = new AgentRoleManager(catalog, new TerminalUi(true, false), name => name == "reviewer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync(["remove", "reviewer"]));
        Assert.True(catalog.Get("reviewer").ReadOnly);
    }
    [Fact]
    public void ImportReloadResetAndInvalidBatchPreserveAuthoritativeConfiguration()
    {
        using var w = new TestWorkspace(); var directory = Path.Combine(w.Root, "data");
        using (var store = new DataStore(directory))
        {
            var catalog = new AgentRoleCatalog(store);
            catalog.Import("[{\"name\":\"reviewer\",\"description\":\"Review\",\"instructions\":\"CUSTOM-ROLE-PROOF\",\"readOnly\":true,\"model\":\"review-model\"}]");
            Assert.Equal("review-model", catalog.Get("reviewer").Model); Assert.True(catalog.Get("reviewer").ReadOnly);
            var original = store.GetSetting("agents:roles");
            Assert.Throws<ArgumentException>(() => catalog.Import("[{\"name\":\"explore\",\"description\":\"unsafe\",\"instructions\":\"unsafe\",\"readOnly\":false}]"));
            Assert.Throws<ArgumentException>(() => catalog.Import("[{\"name\":\"bad\\n\",\"description\":\"bad\",\"instructions\":\"bad\"}]"));
            Assert.Throws<ArgumentException>(() => catalog.Import("[{\"name\":\"reviewer\",\"description\":\"bad\",\"instructions\":\"bad\",\"permissions\":[{\"action\":\"read\",\"resource\":\"*\",\"effect\":\"typo\"}]}]"));
            Assert.Equal(original, store.GetSetting("agents:roles"));
            catalog.Import("[{\"name\":\"general\",\"description\":\"custom\",\"instructions\":\"custom\",\"model\":\"configured\"}]");
            Assert.Equal("configured", catalog.Get("general").Model); catalog.Remove("general"); Assert.Null(catalog.Get("general").Model);
        }
        using var reopened = new DataStore(directory); var restored = new AgentRoleCatalog(reopened);
        Assert.Equal("CUSTOM-ROLE-PROOF", restored.Get("reviewer").Instructions); restored.Remove("reviewer");
        Assert.Throws<ArgumentException>(() => restored.Get("reviewer")); Assert.Equal(2, restored.List().Count);
    }
}
