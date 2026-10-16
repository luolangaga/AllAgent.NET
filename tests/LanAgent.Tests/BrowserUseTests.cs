using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Data;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class BrowserUseTests
{
    [Fact]
    public async Task ExplicitModeOverrideRoutesFollowingCallsToActiveBrowser()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new BrowserFixture();
        var state = new BrowserUseState { Root = fixture.Profile };
        state.Configure(new(Mode: "chromium"));
        await using var tools = new BrowserUseTools(state);
        var open = await tools.OpenAsync(fixture.Url, mode: "builtin");
        Assert.True(open.Ok, open.Error);
        foreach (var result in new[] { await tools.StateAsync(), await tools.EvaluateAsync("document.title"), await tools.CloseAsync() })
        {
            Assert.True(result.Ok, result.Error);
            Assert.Equal("builtin", JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("browser").GetProperty("mode").GetString());
        }
        Assert.Equal("chromium", state.Snapshot().Configuration.Mode);
        var savedDefault = await tools.OpenAsync();
        Assert.False(savedDefault.Ok);
        Assert.Contains("manual connection", savedDefault.Error);
    }
    [Fact]
    public async Task WebViewWindowRemainsResponsiveWhileIdleAndWhileHostIsNotReadingOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new BrowserFixture();
        var state = new BrowserUseState();
        var start = BoundedProcess.StartInfo(state.WorkerPath, ["--profile", fixture.Profile]);
        start.RedirectStandardInput = true;
        using var process = Process.Start(start)!;
        var stderr = BoundedProcess.DrainAsync(process.StandardError, 2000, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        async Task<JsonElement> Read()
        {
            string? line;
            do { line = await process.StandardOutput.ReadLineAsync(timeout.Token); } while (line != null && !line.StartsWith("LANBROWSER:"));
            Assert.NotNull(line);
            using var json = JsonDocument.Parse(line![11..]);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), line);
            return json.RootElement.GetProperty("data").Clone();
        }
        async Task<JsonElement> Evaluate(string script)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation = "evaluate", arguments = new { script } }));
            return (await Read()).GetProperty("value");
        }
        try
        {
            await process.StandardInput.WriteLineAsync("{\"operation\":\"open\",\"arguments\":{}}"); await Read();
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(100, timeout.Token); process.Refresh();
                Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
                Assert.True(process.Responding, "Idle stdin must not block the window message pump.");
            }
            await Evaluate("document.body.innerHTML='<div id=large>'+('<span>editor line</span>'.repeat(20000))+'</div><span id=tick>0</span>';window.tickTimer=setInterval(()=>document.querySelector('#tick').textContent=String(Date.now()),16);true");
            var baseline = (await Evaluate("window.__lanagentFeedback_v1.stats.discoveryNodes")).GetDouble();
            await Task.Delay(500, timeout.Token);
            var scanned = (await Evaluate("window.__lanagentFeedback_v1.stats.discoveryNodes")).GetDouble() - baseline;
            Assert.True(scanned < 1000, "Text updates must not rescan the 20,000-node editor: " + scanned);
            await Evaluate("clearInterval(window.tickTimer);true");
            // Deliberately leave a response larger than the pipe buffer unread.
            await process.StandardInput.WriteLineAsync("{\"operation\":\"evaluate\",\"arguments\":{\"script\":\"'x'.repeat(1000000)\"}}");
            await Task.Delay(300, timeout.Token); process.Refresh();
            Assert.True(process.Responding, "Blocked stdout must not block window input.");
            await Read();
            await process.StandardInput.WriteLineAsync("{\"operation\":\"close\"}"); await Read();
            await process.WaitForExitAsync(timeout.Token);
        }
        finally { BoundedProcess.Kill(process); await process.WaitForExitAsync(); await stderr; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortLivedFeedbackIsRetainedAndUnchangedSubmitDoesNotLoop(bool external)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new BrowserFixture();
        Process? browser = null;
        var state = new BrowserUseState { Root = fixture.Profile };
        if (external)
        {
            var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            var port = BrowserFixture.FreePort();
            var launch = BrowserUseManager.BrowserStartInfo(edge, port, fixture.Profile);
            // A fresh Edge profile's sync/sign-in dialog can consume pointer events.
            // This fixture tests CDP feedback, not browser onboarding.
            launch.ArgumentList.Add("--headless=new"); launch.ArgumentList.Add("--disable-sync");
            browser = Process.Start(launch)!;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                try { await BrowserUseManager.ProbeAsync("http://127.0.0.1:" + port, startup.Token); break; }
                catch (HttpRequestException) { await Task.Delay(100, startup.Token); }
            }
            state.Configure(new(Mode: "chromium", CdpUrl: "http://127.0.0.1:" + port));
        }
        try
        {
            await using var tools = new BrowserUseTools(state);
            Assert.True((await tools.OpenAsync(fixture.Url)).Ok);
            var setup = await tools.EvaluateAsync("""
                document.body.innerHTML='<label>Answer<input id="answer"></label><button id="save">Save</button><p id="saved"></p>';
                window.frameworkValue='';window.clickCount=0;
                document.querySelector('#answer').addEventListener('input',e=>{if(e.isTrusted)window.frameworkValue=e.target.value;});
                document.querySelector('#save').onclick=()=>{
                  window.clickCount++;
                  setTimeout(()=>{const toast=document.createElement('div');toast.setAttribute('role','alert');
                    toast.textContent=window.frameworkValue==='correct'?'Saved successfully':'Validation failed: answer was not updated';
                    document.body.append(toast);setTimeout(()=>toast.remove(),100);},100);
                  if(window.frameworkValue==='correct')document.querySelector('#saved').textContent='Persisted correct';
                };true
                """);
            Assert.True(setup.Ok, setup.Error);
            for (var i = 0; i < 2; i++)
            {
                var page = JsonSerializer.SerializeToElement((await tools.StateAsync()).Data, ToolResult.JsonOptions);
                var click = await tools.ActionAsync("click", Index(page, "button"));
                Assert.True(click.Ok, click.Error);
                Assert.True(click.ToJson().Contains("Validation failed"), click.ToJson() + " debug=" + (await tools.EvaluateAsync("({clicks:window.clickCount,stats:window.__lanagentFeedback_v1.stats,body:document.body.innerText})")).ToJson());
            }
            await Task.Delay(2200); // Message has gone before the next model/state request.
            var after = await tools.StateAsync(); Assert.True(after.Ok, after.Error);
            var observed = JsonSerializer.SerializeToElement(after.Data, ToolResult.JsonOptions);
            Assert.DoesNotContain("Validation failed", observed.GetProperty("dom").GetString());
            Assert.Contains("Validation failed", observed.GetProperty("recentMessages").ToString());
            var blocked = await tools.ActionAsync("click", Index(observed, "button"));
            Assert.False(blocked.Ok); Assert.Contains("Stopped repeating", blocked.Error);
            Assert.True((await tools.StateAsync()).Ok); // An ordinary error must not close the session.
            var count = await tools.EvaluateAsync("window.clickCount"); Assert.Contains("2", count.ToJson());
            var pageAfter = JsonSerializer.SerializeToElement((await tools.StateAsync()).Data, ToolResult.JsonOptions);
            Assert.True((await tools.ActionAsync("input", Index(pageAfter, "input"), "correct")).Ok);
            pageAfter = JsonSerializer.SerializeToElement((await tools.StateAsync()).Data, ToolResult.JsonOptions);
            var saved = await tools.ActionAsync("click", Index(pageAfter, "button"));
            Assert.True(saved.Ok, saved.Error); Assert.Contains("Saved successfully", saved.ToJson());
            Assert.Contains("Persisted correct", (await tools.StateAsync()).ToJson());
            var bridge = new BrowserSkillTools(tools);
            async Task<string> Locate(string selector)
            {
                var result = await bridge.ExecuteAsync("locate", JsonSerializer.Serialize(new { selector }));
                Assert.True(result.Ok, result.Error);
                return JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("ref").GetString()!;
            }
            var answerRef = await Locate("#answer"); var saveRef = await Locate("#save");
            // Replace the input DOM node: the logical reference must find the new one.
            Assert.True((await tools.EvaluateAsync("const old=document.querySelector('#answer');const replacement=old.cloneNode();old.replaceWith(replacement);replacement.addEventListener('input',e=>{if(e.isTrusted)window.frameworkValue=e.target.value;});true")).Ok);
            var batch = await bridge.ExecuteAsync("fill_fields", JsonSerializer.Serialize(new { fields = new[] { new { @ref = answerRef, text = "wrong" } } }));
            Assert.True(batch.Ok, batch.Error);
            var errorResult = await bridge.ExecuteAsync("click_and_wait", JsonSerializer.Serialize(new { @ref = saveRef, success_text = "Saved successfully", timeout_ms = 1500 }));
            Assert.True(errorResult.Ok, errorResult.Error); Assert.Contains("error_observed", errorResult.ToJson());
            batch = await bridge.ExecuteAsync("fill_fields", JsonSerializer.Serialize(new { fields = new[] { new { @ref = answerRef, text = "correct" } } }));
            Assert.True(batch.Ok, batch.Error);
            var savedResult = await bridge.ExecuteAsync("click_and_wait", JsonSerializer.Serialize(new { @ref = saveRef, success_text = "Saved successfully", timeout_ms = 1500 }));
            Assert.True(savedResult.Ok, savedResult.Error); Assert.True(savedResult.ToJson().Contains("success_observed"), savedResult.ToJson() + " framework=" + (await tools.EvaluateAsync("window.frameworkValue")).ToJson());
            var scoped = await bridge.ExecuteAsync("observe", "{\"selector\":\"#saved\",\"limit\":100}");
            Assert.True(scoped.Ok, scoped.Error); Assert.Contains("Persisted correct", scoped.ToJson());
            var partial = await bridge.ExecuteAsync("fill_fields", JsonSerializer.Serialize(new { fields = new[] { new { @ref = answerRef, text = "partial" }, new { @ref = saveRef, text = "cannot-type-into-button" } } }));
            Assert.False(partial.Ok);
            var progress = JsonSerializer.SerializeToElement(partial.Data, ToolResult.JsonOptions);
            Assert.Single(progress.GetProperty("completed").EnumerateArray());
            Assert.Equal(saveRef, progress.GetProperty("failedRef").GetString());
            Assert.Contains("partial", (await tools.EvaluateAsync("document.querySelector('#answer').value")).ToJson());
            Assert.True((await tools.EvaluateAsync("window.noopClicks=0;document.querySelector('#save').onclick=()=>window.noopClicks++;true")).Ok);
            var unknown = await bridge.ExecuteAsync("click_and_wait", JsonSerializer.Serialize(new { @ref = saveRef, success_text = "never displayed", timeout_ms = 200 }));
            Assert.True(unknown.Ok, unknown.Error); Assert.Contains("unknown", unknown.ToJson());
            var attempts = JsonSerializer.SerializeToElement((await tools.EvaluateAsync("window.noopClicks")).Data, ToolResult.JsonOptions);
            Assert.Equal(1, attempts.GetProperty("value").GetInt32());
            Assert.True((await tools.CloseAsync()).Ok);
        }
        finally
        {
            if (browser != null) { BoundedProcess.Kill(browser); await browser.WaitForExitAsync(); browser.Dispose(); }
        }
    }

    [Fact]
    public async Task DefaultOnUserSwitchPersistsAndDeniesBeforeApproval()
    {
        var root = Path.Combine(Path.GetTempPath(), "LanAgent.BrowserTests", Guid.NewGuid().ToString("N"));
        using var store = new DataStore(root);
        var state = new BrowserUseState(); state.Load(store);
        Assert.True(state.Snapshot().Configuration.Enabled);
        var child = new AgentOptions { BrowserUse = state } with { IsSubAgent = true };
        var old = child.BrowserUse.Snapshot();
        state.Save(store, old.Configuration with { Enabled = false });
        Assert.True(old.Token.IsCancellationRequested);
        var restored = new BrowserUseState(); restored.Load(store); Assert.False(restored.Snapshot().Configuration.Enabled);
        await using var tools = new BrowserUseTools(state);
        var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(true); }); registry.Register(tools);
        var result = await registry.CallAsync("browser_open", JsonSerializer.SerializeToElement(new { }), default);
        Assert.False(result.Ok); Assert.Contains("disabled", result.Error); Assert.Equal(0, approvals);
        Assert.True((await tools.StatusAsync()).Ok);
        Assert.Contains(CommandCatalog.Builtins, c => c.Text == "/browser");
    }

    [Fact]
    public async Task BrowserContractLoadsThroughReservedSkillAndCompetingSkillsAreBlocked()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        var other = catalog.Create("agent-browser", "browser automation", "External browser instructions");
        Assert.Throws<ArgumentException>(() => catalog.Create("browser-use", "User override", "Untrusted replacement instructions"));
        var overrideDirectory = Path.Combine(catalog.ProjectDirectory, "browser-use"); Directory.CreateDirectory(overrideDirectory);
        File.WriteAllText(Path.Combine(overrideDirectory, "SKILL.md"), "---\nname: browser-use\ndescription: User override\n---\nUntrusted replacement instructions");
        var builtin = catalog.Get("browser-use", true);
        Assert.Equal("builtin", builtin.Scope); Assert.Contains("operation=\"evaluate\"", builtin.Instructions);
        Assert.DoesNotContain("Untrusted replacement", builtin.Instructions);
        Assert.False(catalog.Get("agent-browser").Enabled); Assert.True(File.Exists(other.Path));
        Assert.DoesNotContain("agent-browser:", catalog.Instructions);
        await using var browser = new BrowserUseTools(new BrowserUseState());
        var registry = new ToolRegistry(); registry.Register(new BrowserSkillTools(browser)); registry.Register(new SkillTools(catalog));
        await using var code = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), new UnusedSandbox());
        Assert.DoesNotContain("arguments_json", code.Instructions);
        var denied = await code.CallToolAsync("browser", JsonSerializer.SerializeToElement(new { operation = "status" }), default);
        Assert.False(denied.Ok); Assert.Contains("skill_read", denied.Error);
        var loaded = await code.CallToolAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "browser-use" }), default);
        Assert.True(loaded.Ok, loaded.Error);
        Assert.True((await code.CallToolAsync("browser", JsonSerializer.SerializeToElement(new { operation = "status" }), default)).Ok);
        Assert.DoesNotContain("arguments_json", code.Instructions); // Full contract never enters the permanent catalog.
    }

    private sealed class UnusedSandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("https://127.0.0.1:9222")]
    [InlineData("http://example.com:9222")]
    [InlineData("ws://user:password@localhost:9222")]
    public void RejectsRemoteOrCredentialedDebugEndpoints(string endpoint) => Assert.Throws<ArgumentException>(() => BrowserUseState.Validate(new(CdpUrl: endpoint)));

    [Fact]
    public void WizardCommandQuotesPathsAsLiteralPowerShellArguments()
    {
        var command = BrowserUseManager.LaunchCommand(@"C:\O'Brien\browser.exe", 9223, @"C:\A B\profile");
        Assert.Contains("O''Brien", command); Assert.Contains("--remote-debugging-port=9223", command);
        Assert.Contains("--user-data-dir='C:\\A B\\profile'", command);
    }

    [Fact]
    public async Task RealWebView2ReadsTypesClicksScreenshotsTabsAndSwitchRevocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new BrowserFixture();
        var state = new BrowserUseState { Root = fixture.Profile };
        await using var tools = new BrowserUseTools(state);
        Assert.True(File.Exists(state.WorkerPath), state.WorkerPath);
        var environment = await tools.CheckEnvironmentAsync(); Assert.True(environment.Ok, environment.Error);
        var open = await tools.OpenAsync(fixture.Url); Assert.True(open.Ok, open.Error);
        var page = await tools.StateAsync(); Assert.True(page.Ok, page.Error);
        var json = JsonSerializer.SerializeToElement(page.Data, ToolResult.JsonOptions);
        Assert.Equal("Browser fixture", json.GetProperty("title").GetString());
        var pageMatches = await tools.UpstreamAsync("search_page", "{\"pattern\":\"Save\"}");
        Assert.True(pageMatches.Ok, pageMatches.Error); Assert.Contains("Save", pageMatches.ToJson());
        var links = await tools.UpstreamAsync("find_elements", "{\"selector\":\"button\",\"include_text\":true}");
        Assert.True(links.Ok, links.Error); Assert.Contains("Save", links.ToJson());
        var found = await tools.UpstreamAsync("find_text", "{\"text\":\"Save\"}"); Assert.True(found.Ok, found.Error);
        page = await tools.StateAsync(); json = JsonSerializer.SerializeToElement(page.Data, ToolResult.JsonOptions);
        var input = Index(json, "input");
        var typed = await tools.ActionAsync("input", input, "LanAgent"); Assert.True(typed.Ok, typed.Error);
        var stale = await tools.ActionAsync("click", input); Assert.False(stale.Ok);
        page = await tools.StateAsync(); json = JsonSerializer.SerializeToElement(page.Data, ToolResult.JsonOptions);
        var clicked = await tools.ActionAsync("click", Index(json, "button")); Assert.True(clicked.Ok, clicked.Error);
        var value = await tools.EvaluateAsync("document.querySelector('#result').textContent");
        Assert.True(value.Ok, value.Error); Assert.Contains("Saved LanAgent", value.ToJson());
        var asyncScript = await tools.EvaluateAsync("(async () => ({title:document.title,count:await Promise.resolve(42)}))()");
        Assert.True(asyncScript.Ok, asyncScript.Error); Assert.Contains("42", asyncScript.ToJson());
        var badScript = await tools.EvaluateAsync("throw new Error('fixture-js-error')"); Assert.False(badScript.Ok); Assert.Contains("fixture-js-error", badScript.Error);
        open = await tools.OpenAsync(fixture.Url); Assert.True(open.Ok, open.Error);
        var schedule = await tools.EvaluateAsync("setTimeout(()=>alert('fixture-dialog'),0); true"); Assert.True(schedule.Ok, schedule.Error);
        await Task.Delay(150);
        var dialogState = await tools.StateAsync(); Assert.True(dialogState.Ok, dialogState.Error); Assert.Contains("fixture-dialog", dialogState.ToJson());
        var dialog = await tools.DialogAsync(true); Assert.True(dialog.Ok, dialog.Error);
        var image = await tools.ScreenshotAsync(); Assert.True(image.Ok, image.Error); Assert.Single(image.Images);
        var tab = await tools.NewTabAsync(fixture.Url); Assert.True(tab.Ok, tab.Error);
        page = await tools.StateAsync(); json = JsonSerializer.SerializeToElement(page.Data, ToolResult.JsonOptions);
        Assert.Equal(2, json.GetProperty("tabs").GetArrayLength());
        var closeTab = await tools.ActionAsync("close_tab", tabId: json.GetProperty("activeTabId").GetString()!); Assert.True(closeTab.Ok, closeTab.Error);
        state.Configure(new(false));
        Assert.False((await tools.OpenAsync(fixture.Url)).Ok);
        state.Configure(new(true));
        open = await tools.OpenAsync(fixture.Url); Assert.True(open.Ok, open.Error);
        Assert.True((await tools.CloseAsync()).Ok);
    }

    [Fact]
    public async Task ExternalCdpDisconnectPreservesBrowserAndWizardDetectsIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        Assert.True(File.Exists(edge), "This live integration test requires Microsoft Edge.");
        await using var fixture = new BrowserFixture();
        var port = BrowserFixture.FreePort(); var endpoint = "http://127.0.0.1:" + port;
        var info = BoundedProcess.StartInfo(edge, ["--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--user-data-dir=" + fixture.Profile, "--no-first-run", "about:blank"]);
        using var process = Process.Start(info)!;
        var stdout = BoundedProcess.DrainAsync(process.StandardOutput, 1000, default); var stderr = BoundedProcess.DrainAsync(process.StandardError, 1000, default);
        try
        {
            var connected = false;
            for (var i = 0; i < 50 && !connected; i++)
            {
                try { await BrowserUseManager.ProbeAsync(endpoint); connected = true; }
                catch (HttpRequestException) { await Task.Delay(200); }
            }
            Assert.True(connected);
            var state = new BrowserUseState(); state.Configure(new(Mode: "chromium", CdpUrl: endpoint));
            string activeTab;
            LanAgent.Core.Browser.CdpConnection authorized;
            await using (var firstTurn = new BrowserUseTools(state))
            {
                Assert.True((await firstTurn.OpenAsync(fixture.Url)).Ok);
                Assert.True((await firstTurn.EvaluateAsync("window.unsavedAnswer='keep-me';document.querySelector('#name').value='Unsaved';true")).Ok);
                authorized = (await state.ExternalAsync(default)).Connection;
                activeTab = authorized.ActiveTabId;
                var error = await firstTurn.EvaluateAsync("throw new Error('ordinary-page-error')");
                Assert.False(error.Ok); Assert.True(authorized.IsConnected);
                Assert.True((await firstTurn.StateAsync()).Ok);
                var tab = await firstTurn.NewTabAsync(fixture.Url); Assert.True(tab.Ok, tab.Error);
                Assert.True((await firstTurn.StateAsync()).Ok);
                Assert.True((await firstTurn.ActionAsync("switch_tab", tabId: activeTab)).Ok);
                Assert.Equal(activeTab, authorized.ActiveTabId);
            }
            await using (var tools = new BrowserUseTools(state))
            {
                var open = await tools.OpenAsync(); Assert.True(open.Ok, open.Error);
                Assert.Same(authorized, (await state.ExternalAsync(default)).Connection);
                Assert.Equal(activeTab, authorized.ActiveTabId);
                Assert.Contains("keep-me", (await tools.EvaluateAsync("window.unsavedAnswer")).ToJson());
                var page = await tools.StateAsync(); Assert.True(page.Ok, page.Error); Assert.Contains("Browser fixture", page.ToJson());
                Assert.True((await tools.CloseAsync()).Ok);
            }
            await BrowserUseManager.ProbeAsync(endpoint); // Closing/disposal must not close user's external browser.
            Assert.False(process.HasExited);
        }
        finally { BoundedProcess.Kill(process); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }

    private static int Index(JsonElement page, string tag)
    {
        var match = Regex.Match(page.GetProperty("dom").GetString()!, @"\[(\d+)\] <" + tag);
        Assert.True(match.Success, page.ToString()); return int.Parse(match.Groups[1].Value);
    }
    private sealed class BrowserFixture : IAsyncDisposable
    {
        private readonly HttpListener _http = new();
        private readonly Task _serve;
        public string Profile { get; } = Path.Combine(Path.GetTempPath(), "LanAgent.BrowserTests", Guid.NewGuid().ToString("N"));
        public string Url { get; } = "http://127.0.0.1:" + FreePort() + "/";
        public BrowserFixture() { _http.Prefixes.Add(Url); _http.Start(); _serve = ServeAsync(); }
        public static int FreePort() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return port; }
        private async Task ServeAsync()
        {
            try
            {
                while (_http.IsListening)
                {
                    var context = await _http.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes("<html><head><title>Browser fixture</title></head><body><label>Name<input id='name'></label><button onclick=\"document.querySelector('#result').textContent='Saved '+document.querySelector('#name').value\">Save</button><p id='result'></p></body></html>");
                    context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _http.Close(); await _serve;
            // Only this fixture-created profile is removed; WebView2 may briefly retain handles.
            if (Path.GetFullPath(Profile).StartsWith(Path.Combine(Path.GetTempPath(), "LanAgent.BrowserTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                for (var i = 0; i < 20 && Directory.Exists(Profile); i++)
                    try { Directory.Delete(Profile, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}

