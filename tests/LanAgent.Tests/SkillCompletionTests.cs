using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class SkillCompletionTests
{
    [Theory]
    [InlineData("/skills")]
    [InlineData("/SKILLS")]
    public void ExactBuiltInCommandWinsOverSkillSuggestions(string text)
    {
        var state = new InputState(); state.Set(text);
        var hints = new[] { new CommandHint("/skill-creator", "Create", IsSkill: true), new CommandHint("/skills", "Manage skills") };
        Assert.Equal("/skills", state.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), hints, []));
        Assert.Equal(text, state.Text);
    }
    [Fact]
    public void EnterCompletesHighlightedSkillInsteadOfFirstSkill()
    {
        var state = new InputState(); state.Set("/de");
        var hints = new[] { new CommandHint("/demo", "First", IsSkill: true), new CommandHint("/design", "Second", IsSkill: true) };
        state.Key(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), hints, []);
        Assert.Null(state.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), hints, []));
        Assert.Equal("/design ", state.Text);
    }
    [Fact]
    public void HighlightedCommandIsNotReplacedBySkill()
    {
        var state = new InputState(); state.Set("/sk");
        var hints = new[] { new CommandHint("/skills", "Manage"), new CommandHint("/skill-creator", "Create", IsSkill: true) };
        Assert.Equal("/skills", state.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), hints, []));
    }
    [Theory]
    [InlineData("/demo")]
    [InlineData("/de")]
    public void EnterCompletesSkillAndLeavesRoomForPrompt(string text)
    {
        var state = new InputState(); state.Set(text);
        var hints = new[] { new CommandHint("/demo", "Demo", "任务参数", IsSkill: true) };
        Assert.Null(state.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), hints, []));
        Assert.Equal("/demo ", state.Text);
        state.Insert("do something");
        Assert.Equal("/demo do something", state.Key(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), [], []));
    }
}
