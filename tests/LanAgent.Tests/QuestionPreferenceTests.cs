using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class QuestionPreferenceTests
{
    [Fact]
    public void FourLevelsPreserveDefaultPromptAndAddDistinctPreferences()
    {
        const string original = "Project instructions\nAvailable child agents:\nworker: test";
        Assert.Equal(4, QuestionPreference.Levels.Length);
        Assert.Equal(original, QuestionPreference.AppendInstructions(original, "default"));
        var prompts = QuestionPreference.Levels.Skip(1).Select(level => QuestionPreference.AppendInstructions(original, level)).ToArray();
        Assert.Equal(3, prompts.Distinct().Count());
        Assert.All(prompts, prompt =>
        {
            Assert.StartsWith(original + "\n\nQuestion preference:\n", prompt);
            Assert.Contains("required approvals", prompt);
        });
        Assert.Equal(original, QuestionPreference.AppendInstructions(original, "unknown"));
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
    }
}
