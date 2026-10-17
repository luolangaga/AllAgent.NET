using AllAgent.NET.Terminal;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using System.Text;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Terminal interaction")]
public sealed class WorkspaceNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTerminalSwitchesProjectsWithoutChangingAnotherTerminalOrOldHistory(bool explicitStorage)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var first = Path.Combine(workspace.Root, "first");
        var second = Path.Combine(workspace.Root, "second project");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var firstData = explicitStorage ? Path.Combine(workspace.Root, "shared-data") : Path.Combine(first, ".allagent", "data");
        var secondData = explicitStorage ? firstData : Path.Combine(second, ".allagent", "data");
        using (var store = new DataStore(firstData))
        {
            var config = new ApiConfig { DisplayName = "WORKSPACE-PROVIDER", DefaultModel = "workspace-model", Models = ["workspace-model"], IsEnabled = true };
            store.AddApiConfig(config); store.SetSetting("terminal:lastConfig", config.Id.ToString());
        }
        Directory.CreateDirectory(Path.Combine(first, ".allagent"));
        var oldInstructions = Path.Combine(first, ".allagent", "AGENT.md");
        File.WriteAllText(oldInstructions, "OLD-PROJECT");
        var command = "dotnet " + TerminalSession.Quote(typeof(TerminalSession).Assembly.Location)
            + " --no-initialize --ask --workspace " + TerminalSession.Quote(first)
            + (explicitStorage ? " --data-dir " + TerminalSession.Quote(firstData) : "");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var terminal = new PseudoConsole(command, 160, 40);
        using var other = new PseudoConsole(command, 160, 40);
        var text = new StringBuilder();
        var read = Task.Run(() =>
        {
            var bytes = new byte[16384]; var decoder = Encoding.UTF8.GetDecoder(); var chars = new char[16384]; int count;
            try { while ((count = terminal.Output.Read(bytes)) > 0) { var length = decoder.GetChars(bytes, 0, count, chars, 0); lock (text) text.Append(chars, 0, length); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        var drainOther = Task.Run(() =>
        {
            try { other.Output.CopyTo(Stream.Null); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        try
        {
            await Wait(() => Contains("workspace-model"));
            await Wait(() => Count(firstData, first) == 2);
            Send("/workspace missing-folder");
            await Wait(() => Contains("missing-folder"));
            Assert.False(terminal.HasExited);
            Send("/workspace"); await Wait(() => Contains("当前工作目录"));
            Send("/workspace \"" + second + "\\\"");
            await Wait(() => Count(secondData, second) == 1);
            Send("/memory set NEW-PROJECT-INSTRUCTIONS");
            var nextInstructions = Path.Combine(second, ".allagent", "AGENT.md");
            await Wait(() => File.Exists(nextInstructions) && File.ReadAllText(nextInstructions) == "NEW-PROJECT-INSTRUCTIONS");
            Assert.Equal("OLD-PROJECT", File.ReadAllText(oldInstructions));
            Assert.False(other.HasExited);
            Send("/workspace ."); await Wait(() => Contains("当前已在此工作目录"));
            Assert.Equal(1, Count(secondData, second));
            Send("/workspace ../first");
            await Wait(() => Count(firstData, first) == 3);
            Assert.Equal(1, Count(secondData, second));
            Assert.False(other.HasExited);
        }
        finally
        {
            terminal.CloseSession(); other.CloseSession();
            await Task.WhenAny(Task.WhenAll(read, drainOther), Task.Delay(2000));
        }
        void Send(string value) => terminal.Input.Write(Encoding.UTF8.GetBytes(value + "\r"));
        bool Contains(string value) { lock (text) return text.ToString().Contains(value); }
        int Count(string directory, string root)
        {
            if (!File.Exists(Path.Combine(directory, "allagent.json"))) return 0;
            using var store = new DataStore(directory);
            return store.Conversations.Count(c => c.WorkspaceRoot == root);
        }
        async Task Wait(Func<bool> condition)
        {
            while (!condition())
            {
                try { await Task.Delay(50, timeout.Token); }
                catch (OperationCanceledException) { lock (text) throw new Exception(text.ToString().Replace("\u001b", "<ESC>")); }
            }
        }
    }

    [Fact]
    public void PathsResolveRelativeToCurrentWorkspaceAndRequireExistingFolders()
    {
        using var workspace = new TestWorkspace();
        var folder = Path.Combine(workspace.Root, "folder with spaces"); Directory.CreateDirectory(folder);
        Assert.Equal(folder, WorkspaceNavigation.Resolve("folder with spaces", workspace.Root, false));
        Assert.Equal(workspace.Root, WorkspaceNavigation.Resolve("..", folder, false));
        Assert.Throws<DirectoryNotFoundException>(() => WorkspaceNavigation.Resolve("missing", workspace.Root, true));
        File.WriteAllText(Path.Combine(workspace.Root, "file.txt"), "file");
        Assert.Throws<DirectoryNotFoundException>(() => WorkspaceNavigation.Resolve("file.txt", workspace.Root, true));
    }

    [Theory]
    [InlineData("/workspace", null)]
    [InlineData("/workspace D:\\My Project", "D:\\My Project")]
    [InlineData("/workspace \"D:\\My Project\\\"", "D:\\My Project\\")]
    [InlineData("/workspace 'D:\\My Project'", "D:\\My Project")]
    [InlineData("/workspace D:\\Bob's Project", "D:\\Bob's Project")]
    public void CommandTreatsRemainderAsLiteralFolder(string command, string? expected)
    {
        Assert.Equal(expected, WorkspaceNavigation.ParseCommand(command).ElementAtOrDefault(1));
    }

    [Fact]
    public void NewArgumentsResetSessionKeepExplicitStorageAndCurrentPermission()
    {
        using var workspace = new TestWorkspace();
        var raw = new[] { "chat", "--workspace", workspace.Root, "--data-dir", "relative-data", "--resume", "old", "--config", "old-provider", "--ask", "--inline", "--no-initialize" };
        var previous = TerminalArguments.Parse(raw);
        var next = TerminalArguments.Parse(WorkspaceNavigation.Arguments(raw, previous, workspace.Root, true));
        Assert.Equal(previous.DataDirectory, next.DataDirectory);
        Assert.Null(next.Get("resume")); Assert.Null(next.Get("config"));
        Assert.DoesNotContain("ask", next.Flags); Assert.Contains("inline", next.Flags);
        Assert.Contains("no-initialize", next.Flags); Assert.Contains("full-access", next.Flags);
        var id = Guid.NewGuid();
        var recovery = TerminalArguments.Parse(WorkspaceNavigation.Arguments(raw, previous, workspace.Root, false, id));
        Assert.Equal(id.ToString(), recovery.Get("resume")); Assert.Contains("ask", recovery.Flags);
    }

    [Fact]
    public void DefaultStorageFollowsTargetWorkspaceAndOptionsPrecedeSeparator()
    {
        using var workspace = new TestWorkspace();
        var raw = new[] { "chat", "--", "--workspace-is-literal" };
        var next = TerminalArguments.Parse(WorkspaceNavigation.Arguments(raw, TerminalArguments.Parse(raw), workspace.Root, false));
        Assert.Equal(workspace.Root, next.Workspace);
        Assert.Equal(Path.Combine(workspace.Root, ".allagent", "data"), next.DataDirectory);
        Assert.Equal(new[] { "--workspace-is-literal" }, next.Positionals);
        Assert.Contains(CommandCatalog.Common, command => command.Text == "/workspace");
    }

    [Fact]
    public void OptionValuesAreNotInterpretedAsFlagsDuringSwitch()
    {
        using var workspace = new TestWorkspace();
        var raw = new[] { "chat", "--model", "--ask", "--name", "--workspace" };
        var next = TerminalArguments.Parse(WorkspaceNavigation.Arguments(raw, TerminalArguments.Parse(raw), workspace.Root, true));
        Assert.Equal("--ask", next.Get("model"));
        Assert.Equal("--workspace", next.Get("name"));
        Assert.DoesNotContain("ask", next.Flags);
    }
}
