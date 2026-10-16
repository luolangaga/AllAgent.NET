using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanAgent.Core.Data;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

[Collection("Terminal interaction")]
public sealed class BrowserPanelTests
{
    [Fact]
    public async Task SavedCustomDirectoryReconnectsAfterRestartWithoutDirectoryPrompt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        var root = Path.Combine(workspace.Root, "remembered-custom-root");
        var start = BrowserUseManager.BrowserStartInfo(edge, 9222, root);
        start.ArgumentList.Remove("--remote-debugging-port=9222"); start.ArgumentList.Add("--remote-debugging-port=0"); start.ArgumentList.Add("--disable-sync");
        using var browser = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var profile = Path.Combine(root, "Default");
            while (BrowserProfiles.DebugEndpoint(root) == null || !File.Exists(Path.Combine(profile, "Preferences"))) await Task.Delay(100, timeout.Token);
            var data = Path.Combine(workspace.Root, "data");
            using (var store = new DataStore(data))
            {
                new BrowserUseState().Save(store, new(Mode: "chromium", CdpUrl: "ws://127.0.0.1:1/devtools/browser/expired")
                    { BrowserName = "Microsoft Edge", ProfileDirectory = profile, TabId = "expired-tab" });
                BrowserUseManager.RememberProfile(store, "Microsoft Edge", edge, BrowserProfiles.FromPath(profile));
            }
            var assembly = Environment.GetEnvironmentVariable("LANAGENT_TEST_TERMINAL") ?? typeof(TerminalSession).Assembly.Location;
            using var terminal = new PseudoConsole("dotnet " + TerminalSession.Quote(assembly) + " browser reconnect --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(data), 110, 32);
            var output = new StringBuilder();
            var reader = Task.Run(() =>
            {
                using var stream = new StreamReader(terminal.Output, Encoding.UTF8); var chars = new char[4096];
                try { int count; while ((count = stream.Read(chars, 0, chars.Length)) > 0) lock (output) output.Append(chars, 0, count); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            try
            {
                while (!terminal.HasExited) await Task.Delay(100, timeout.Token);
                Assert.Equal(0, terminal.ExitCode);
            }
            finally { terminal.CloseSession(); await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
            lock (output)
            {
                Assert.Contains("页面控制验证通过", output.ToString());
                Assert.DoesNotContain("选择 AI 使用的浏览器配置", output.ToString());
                Assert.DoesNotContain("粘贴已有配置目录", output.ToString());
            }
            using var reopened = new DataStore(data);
            Assert.Equal(profile, reopened.GetSetting("browser:profile"));
            Assert.Equal(BrowserProfiles.DebugEndpoint(root), reopened.GetSetting("browser:cdp"));
            Assert.NotEqual("expired-tab", reopened.GetSetting("browser:tab"));
        }
        finally { await StopFixtureBrowserAsync(browser, root); }
    }

    [Fact]
    public async Task SelectedProfileReconnectsThroughItsOwnDebugFileAndTab()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        var root = Path.Combine(workspace.Root, "existing-profile-test");
        var start = BrowserUseManager.BrowserStartInfo(edge, 9222, root, "Default");
        start.ArgumentList.Remove("--remote-debugging-port=9222");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--disable-sync");
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            while (BrowserProfiles.DebugEndpoint(root) == null || !File.Exists(Path.Combine(root, "Default", "Preferences")))
                await Task.Delay(100, deadline.Token);
            var profile = BrowserProfiles.FromPath(Path.Combine(root, "Default"));
            await using var connectionPage = new BrowserConnectionPage("edge");
            var marker = connectionPage.Url;
            using var launch = Process.Start(BrowserUseManager.ExistingProfileStartInfo(edge, profile, "edge", marker));
            var endpoint = BrowserProfiles.DebugEndpoint(root)!;
            string tabId;
            var attempts = 0;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await using var probe = await LanAgent.Core.Browser.CdpConnection.ConnectAsync(endpoint, deadline.Token);
                var tabs = await probe.TabsAsync(deadline.Token);
                if (tabs.Any(t => t.Url == marker)) break;
                if (++attempts == 30) Assert.Fail("Selected profile marker not opened: " + string.Join(";", tabs.Select(t => t.Url)));
                await Task.Delay(100, deadline.Token);
            }
            var state = new BrowserUseState();
            LanAgent.Core.Browser.CdpConnection? authorized = null;
            tabId = await BrowserUseManager.VerifyControlAsync(endpoint, deadline.Token, showReadyPage: true, preferredUrl: marker, onVerified: async connection =>
            {
                state.Configure(new(Mode: "chromium", CdpUrl: endpoint) { BrowserName = "Microsoft Edge", ProfileDirectory = profile.Path, TabId = connection.ActiveTabId });
                authorized = connection;
                await state.AdoptExternalAsync(connection, deadline.Token);
            });
            await using var tools = new BrowserUseTools(state);
            var open = await tools.OpenAsync(); Assert.True(open.Ok, open.Error);
            Assert.Same(authorized, (await state.ExternalAsync(deadline.Token)).Connection);
            Assert.Equal(tabId, authorized!.ActiveTabId);
            Assert.Contains(BrowserUseManager.ReadyPageTitle, (await tools.EvaluateAsync("document.title")).ToJson());
            Assert.Contains("Default", open.ToJson());
            Assert.Contains("Microsoft Edge", open.ToJson());
            await Assert.ThrowsAsync<InvalidOperationException>(() => LanAgent.Core.Browser.CdpConnection.ConnectAsync(endpoint, deadline.Token, preferredUrl: "about:blank#wrong-profile"));
            Assert.True((await tools.CloseAsync()).Ok);
        }
        finally { await StopFixtureBrowserAsync(process, root); }
    }

    [Fact]
    public async Task SuccessfulConnectionShowsFeedbackAndLeavesMenuInRealTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var pid = await BrowserUseManager.LaunchAndConnectAsync(edge, port, Path.Combine(workspace.Root, "edge-feedback"), _ => { });
        using var browser = Process.GetProcessById(pid);
        try
        {
            var directory = Path.Combine(workspace.Root, "data");
            using (var store = new DataStore(directory)) store.SetSetting("browser:enabled", "off");
            var assembly = Environment.GetEnvironmentVariable("LANAGENT_TEST_TERMINAL") ?? typeof(TerminalSession).Assembly.Location;
            using var terminal = new PseudoConsole("dotnet " + TerminalSession.Quote(assembly) + " browser --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(directory), 110, 32);
            var output = new StringBuilder();
            var reader = Task.Run(() =>
            {
                using var stream = new StreamReader(terminal.Output, Encoding.UTF8);
                var chars = new char[4096];
                try { int length; while ((length = stream.Read(chars, 0, chars.Length)) > 0) lock (output) output.Append(chars, 0, length); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            async Task WaitFor(string text)
            {
                while (true)
                {
                    lock (output) if (output.ToString().Contains(text, StringComparison.Ordinal)) return;
                    await Task.Delay(50, timeout.Token);
                }
            }
            try
            {
                await WaitFor("浏览器管理");
                terminal.Input.Write(Encoding.UTF8.GetBytes("已有调试地址\r")); terminal.Input.Flush();
                await WaitFor("粘贴本机 CDP 地址");
                terminal.Input.Write(Encoding.UTF8.GetBytes("http://127.0.0.1:" + port + "\r")); terminal.Input.Flush();
                await WaitFor("已返回聊天");
                while (!terminal.HasExited) await Task.Delay(50, timeout.Token);
                Assert.Equal(0, terminal.ExitCode);
                lock (output) Assert.Contains("页面控制验证通过", output.ToString());
                using var store = new DataStore(directory);
                Assert.Equal("http://127.0.0.1:" + port, store.GetSetting("browser:cdp"));
                Assert.Equal("off", store.GetSetting("browser:enabled"));
            }
            finally { terminal.CloseSession(); await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally
        {
            await StopFixtureBrowserAsync(browser, Path.Combine(workspace.Root, "edge-feedback"));
        }
    }

    [Fact]
    public async Task BareBrowserCommandOpensManagementPanelInRealTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var terminalAssembly = Environment.GetEnvironmentVariable("LANAGENT_TEST_TERMINAL") ?? typeof(TerminalSession).Assembly.Location;
        var command = "dotnet " + TerminalSession.Quote(terminalAssembly) + " browser --workspace " + TerminalSession.Quote(workspace.Root) + " --data-dir " + TerminalSession.Quote(Path.Combine(workspace.Root, "data"));
        using var terminal = new PseudoConsole(command, 100, 30);
        var output = new StringBuilder();
        var reader = Task.Run(() =>
        {
            using var stream = new StreamReader(terminal.Output, Encoding.UTF8);
            var buffer = new char[4096];
            try { int count; while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) lock (output) output.Append(buffer, 0, count); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                lock (output) if (output.ToString().Contains("浏览器管理", StringComparison.Ordinal)) break;
                await Task.Delay(50, deadline.Token);
            }
            terminal.Input.Write(Encoding.UTF8.GetBytes("返回聊天\r")); terminal.Input.Flush();
            while (!terminal.HasExited) await Task.Delay(50, deadline.Token);
            Assert.Equal(0, terminal.ExitCode);
            lock (output)
            {
                var text = output.ToString();
                Assert.Contains("内置 WebView2", text); Assert.Contains("Microsoft Edge", text); Assert.Contains("自动启动", text);
            }
        }
        finally { terminal.CloseSession(); await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task FullscreenMenuKeepsFailureReasonBesideActionsAtLaptopSize()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24; console.Profile.Capabilities.Interactive = true;
        var ui = new TerminalUi(false, false); using var screen = new FullScreenTerminal(store, catalog, mcp, ui, console);
        var manager = new BrowserUseManager(store, new BrowserUseState(), ui);
        console.Input.PushKey(ConsoleKey.Enter);
        await screen.ChooseAsync("浏览器管理", BrowserUseManager.PanelActions, value => value, default,
            body: manager.PanelBody("连接未成功：调试端口拒绝连接。\n可重试或使用内置模式，原配置保留。"));
        Assert.Contains("调试端口拒绝连接", console.Output); Assert.Contains("原配置保留", console.Output);
        Assert.Contains("Microsoft Edge", console.Output); Assert.DoesNotContain("开始检测连接？", console.Output);
    }

    [Fact]
    public async Task AutomaticEdgeLaunchActuallyExposesCdpWithoutRunningPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        Assert.True(File.Exists(edge));
        var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        var profile = Path.Combine(workspace.Root, "browser profile ' literals");
        var info = BrowserUseManager.BrowserStartInfo(edge, port, profile);
        Assert.False(info.UseShellExecute); Assert.Equal(edge, info.FileName);
        Assert.Contains("--user-data-dir=" + profile, info.ArgumentList);
        var progress = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pid = await BrowserUseManager.LaunchAndConnectAsync(edge, port, profile, progress.Add, deadline.Token);
        using var process = Process.GetProcessById(pid);
        try
        {
            var endpoint = "http://127.0.0.1:" + port;
            await BrowserUseManager.ProbeAsync(endpoint);
            await BrowserUseManager.VerifyControlAsync(endpoint, showReadyPage: true);
            await using var connection = await LanAgent.Core.Browser.CdpConnection.ConnectAsync(endpoint, deadline.Token);
            var proof = await connection.SendAsync("Runtime.evaluate", new { expression = "({title:document.title,text:document.body.innerText})", returnByValue = true }, deadline.Token);
            var page = proof.GetProperty("result").GetProperty("value");
            Assert.Equal(BrowserUseManager.ReadyPageTitle, page.GetProperty("title").GetString());
            Assert.Contains("请回到终端输入任务", page.GetProperty("text").GetString());
            Assert.NotEmpty(progress); Assert.False(process.HasExited);
        }
        finally { await StopFixtureBrowserAsync(process, profile); }
    }

    private static async Task StopFixtureBrowserAsync(Process process, string profile)
    {
        BoundedProcess.Kill(process); await process.WaitForExitAsync();
        var resolved = Path.GetFullPath(profile);
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LanAgent.Tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(testRoot, resolved, StringComparison.OrdinalIgnoreCase);
        // Chromium utility processes can retain database handles briefly after tree termination.
        for (var i = 0; i < 50 && Directory.Exists(resolved); i++)
        {
            try { Directory.Delete(resolved, recursive: true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}
