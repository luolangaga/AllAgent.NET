using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class TerminalSelectionPersistenceTests
{
    [Fact]
    public async Task SelectionSurvivesStoreReopenAndExplicitModelOverridesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        try
        {
            Guid id;
            using (var store = new DataStore(path))
            {
                var config = new ApiConfig { DefaultModel = "first", Models = ["first", "second"], ApiKey = "fixture-key", IsEnabled = true };
                store.AddApiConfig(config); id = config.Id;
                var manager = new ModelManager(store, new TerminalUi(true, false), TerminalArguments.Parse([]));
                Assert.Equal("second", (await manager.SelectAsync(id.ToString(), "second")).DefaultModel);
            }
            using (var store = new DataStore(path))
            {
                store.AddApiConfig(new ApiConfig { DefaultModel = "other", IsEnabled = true });
                var manager = new ModelManager(store, new TerminalUi(true, false), TerminalArguments.Parse([]));
                var selected = await manager.SelectAsync();
                Assert.Equal(id, selected.Id); Assert.Equal("second", selected.DefaultModel);
                Assert.Equal("first", (await manager.SelectAsync(id.ToString(), "first")).DefaultModel);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData("\n你好\r\n世界\t测试", "你好 世界 测试")]
    [InlineData("普通标题", "普通标题")]
    public void SidebarTitlesStayOnOneLine(string input, string expected) => Assert.Equal(expected, SessionOperations.SingleLineTitle(input));
}
