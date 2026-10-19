using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Exercises the web terminal manager over real ConPTY sessions (Windows only).</summary>
public sealed class WebTerminalTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allagent-term-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _frames = [];
    private readonly BridgeEventPusher _pusher = new();
    private readonly WebTerminalManager _manager;

    public WebTerminalTests()
    {
        Directory.CreateDirectory(_root);
        _pusher.SetPusher(json => { lock (_frames) _frames.Add(json); });
        _manager = new WebTerminalManager(_pusher, () => _root);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best effort cleanup */ }
    }

    private Task<string> WaitForOutputAsync(string id, string expected) => WaitForTextAsync(() =>
    {
        var text = new StringBuilder();
        lock (_frames)
        {
            foreach (var frame in _frames)
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (root.GetProperty("eventName").GetString() != "terminal.output") continue;
                if (root.GetProperty("streamId").GetString() != id) continue;
                text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("data").GetProperty("base64").GetString()!)));
            }
        }
        return text.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase) ? text.ToString() : null;
    }, "output containing " + expected);

    private async Task<JsonElement> WaitForEventAsync(string eventName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (_frames)
            {
                foreach (var frame in _frames)
                {
                    using var document = JsonDocument.Parse(frame);
                    if (document.RootElement.GetProperty("eventName").GetString() == eventName)
                        return document.RootElement.GetProperty("data").Clone();
                }
            }
            await Task.Delay(60);
        }
        throw new TimeoutException("Did not observe event " + eventName);
    }

    private static async Task<string> WaitForTextAsync(Func<string?> probe, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value) return value;
            await Task.Delay(60);
        }
        throw new TimeoutException("Did not observe " + what);
    }

    [Fact]
    public async Task StreamsShellOutputAndReplaysItOnAttach()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains(WebTerminalManager.Shells, shell => shell.Id == "cmd");

        var session = _manager.Create("cmd", null, 100, 30);
        Assert.Equal("cmd", session.ShellId);
        Assert.True(session.Alive);

        await _manager.WriteAsync(session.Id, Encoding.UTF8.GetBytes("echo ALLAGENT-TERMINAL-OK\r"), default);
        var output = await WaitForOutputAsync(session.Id, "ALLAGENT-TERMINAL-OK");
        Assert.Contains("ALLAGENT-TERMINAL-OK", output);

        var (info, replay, sequence) = _manager.Attach(session.Id);
        Assert.Equal(session.Id, info.Id);
        Assert.True(sequence > 0);
        Assert.Contains("ALLAGENT-TERMINAL-OK", Encoding.UTF8.GetString(Convert.FromBase64String(replay)), StringComparison.OrdinalIgnoreCase);

        Assert.True(_manager.Resize(session.Id, 80, 24));
    }

    [Fact]
    public async Task HonorstheRequestedWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_root, "sub");
        Directory.CreateDirectory(directory);

        var session = _manager.Create("cmd", directory, 100, 30);
        await _manager.WriteAsync(session.Id, Encoding.UTF8.GetBytes("cd\r"), default);
        // cmd prints the current directory; the prompt is not needed for the assertion.
        await WaitForOutputAsync(session.Id, directory);
    }

    [Fact]
    public async Task ReportsExitCodeWhenTheShellTerminates()
    {
        if (!OperatingSystem.IsWindows()) return;
        var session = _manager.Create("cmd", null, 100, 30);
        await _manager.WriteAsync(session.Id, Encoding.UTF8.GetBytes("exit 0\r"), default);

        var exit = await WaitForEventAsync("terminal.exit");
        Assert.Equal(0, exit.GetProperty("exitCode").GetInt32());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_manager.List().Single(item => item.Id == session.Id).Alive && DateTime.UtcNow < deadline) await Task.Delay(50);
        var stopped = _manager.List().Single(item => item.Id == session.Id);
        Assert.False(stopped.Alive);
        Assert.Equal(0, stopped.ExitCode);
    }

    [Fact]
    public async Task CloseKillsTheSessionAndAnnouncesTheChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        var session = _manager.Create("cmd", null, 100, 30);
        Assert.True(await _manager.CloseAsync(session.Id, default));
        Assert.Empty(_manager.List());
        await WaitForEventAsync("terminal.changed");
        Assert.False(await _manager.CloseAsync(session.Id, default));
    }

    [Fact]
    public void RejectsUnknownSessionsAndBadDirectories()
    {
        Assert.Throws<ArgumentException>(() => _manager.Attach("missing"));
        Assert.Throws<ArgumentException>(() => _manager.Resize("missing", 80, 24));
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => _manager.Create("cmd", Path.Combine(_root, "nope"), 80, 24));
    }
}
