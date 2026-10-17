using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class QuestionPreferenceTests
{
    [Fact]
    public void FourLevelsPreserveDefaultPromptAndAddDistinctPreferences()
    {
        const string original = "Project instructions\nAvailable child agents:\nworker: test";
        Assert.Equal(4, QuestionPreference.Levels.Length);
        Assert.Equal(original, QuestionPreference.AppendInstructions(original, "default"));
        Assert.Equal("", QuestionPreference.PreferenceText("default"));
        var prompts = QuestionPreference.Levels.Skip(1).Select(level => QuestionPreference.AppendInstructions(original, level)).ToArray();
        Assert.Equal(3, prompts.Distinct().Count());
        Assert.All(prompts, prompt =>
        {
            Assert.StartsWith(original + "\n\nQuestion preference:\n", prompt);
            Assert.Contains("required approvals", prompt);
        });
        Assert.Equal(original, QuestionPreference.AppendInstructions(original, "unknown"));
        // Volatile tail carries the same bytes the composed prompt previously embedded.
        Assert.All(QuestionPreference.Levels.Skip(1), level =>
            Assert.Equal(original + "\n\n" + QuestionPreference.PreferenceText(level), QuestionPreference.AppendInstructions(original, level)));
    }

    [Fact]
    public void CommandsPersistAndResetWithoutLeavingPromptInstructions()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var ui = new TerminalUi(true, false);
            using (var store = new DataStore(path))
            {
                Assert.Equal("default", QuestionPreference.Current(store));
                QuestionPreference.Execute(store, ui, ["3"]);
                Assert.Equal("proactive", QuestionPreference.Current(store));
                Assert.Throws<ArgumentException>(() => QuestionPreference.Execute(store, ui, ["invalid"]));
                Assert.Throws<ArgumentException>(() => QuestionPreference.Execute(store, ui, ["default", "extra"]));
                Assert.Equal("proactive", QuestionPreference.Current(store));
                QuestionPreference.Execute(store, ui, ["status"]);
                Assert.Equal("proactive", QuestionPreference.Current(store));
            }
            using (var store = new DataStore(path))
            {
                Assert.Equal("proactive", QuestionPreference.Current(store));
                foreach (var (input, expected) in new[] { ("1", "minimal"), ("2", "balanced"), ("MINIMAL", "minimal"), ("0", "default") })
                {
                    QuestionPreference.Execute(store, ui, [input]);
                    Assert.Equal(expected, QuestionPreference.Current(store));
                }
                Assert.Equal("unchanged", QuestionPreference.AppendInstructions("unchanged", QuestionPreference.Current(store)));
            }
            using (var store = new DataStore(path)) Assert.Equal("default", QuestionPreference.Current(store));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void CommandIsDiscoverableAndReserved()
    {
        Assert.Contains(CommandCatalog.Common, command => command.Text == "/questions");
        Assert.All(QuestionPreference.Levels, level => Assert.Contains(CommandCatalog.Builtins, command => command.Text == "/questions " + level));
        Assert.Throws<ArgumentException>(() => SkillCatalog.ValidateName("questions"));
        Assert.Contains(CommandCatalog.Common, command => command.Text == "/cache");
        Assert.All(CachePreference.TtlLevels, level => Assert.Contains(CommandCatalog.Builtins, command => command.Text == "/cache ttl " + level));
        Assert.Throws<ArgumentException>(() => SkillCatalog.ValidateName("cache"));
    }

    [Fact]
    public void CacheCommandPersistsTtlAndRetentionGlobally()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var ui = new TerminalUi(true, false);
            using (var store = new DataStore(path))
            {
                Assert.Equal("default", CachePreference.CurrentTtl(store));
                Assert.Equal("default", CachePreference.CurrentRetention(store));
                CachePreference.Execute(store, ui, ["ttl", "1h"]);
                Assert.Equal("1h", CachePreference.CurrentTtl(store));
                CachePreference.Execute(store, ui, ["ttl", "5m"]);
                Assert.Equal("default", CachePreference.CurrentTtl(store));
                CachePreference.Execute(store, ui, ["retention", "24h"]);
                Assert.Equal("24h", CachePreference.CurrentRetention(store));
                Assert.Throws<ArgumentException>(() => CachePreference.Execute(store, ui, ["ttl", "2h"]));
                Assert.Throws<ArgumentException>(() => CachePreference.Execute(store, ui, ["ttl"]));
                Assert.Throws<ArgumentException>(() => CachePreference.Execute(store, ui, ["retention"]));
                Assert.Throws<ArgumentException>(() => CachePreference.Execute(store, ui, ["unknown"]));
                Assert.Throws<ArgumentException>(() => CachePreference.Execute(store, ui, ["ttl", "1h", "extra"]));
                CachePreference.Execute(store, ui, ["status"]);
                Assert.Equal("default", CachePreference.CurrentTtl(store));
                Assert.Equal("24h", CachePreference.CurrentRetention(store));
            }
            using (var store = new DataStore(path))
            {
                Assert.Equal("default", CachePreference.CurrentTtl(store));
                Assert.Equal("24h", CachePreference.CurrentRetention(store));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
