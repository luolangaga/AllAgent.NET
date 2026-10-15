using System.Diagnostics;
using System.Text.Json;
using LanAgent.Core.Tools;
using LanAgent.Core.Runtime;
using LanAgent.Terminal;
using LanAgent.Core.Data;
using Xunit;

namespace LanAgent.Tests;

[Collection("Hyperlight acceptance")]
public sealed class ComputerUseTests
{
    [Fact]
    public async Task DisabledOperationsFailBeforeApprovalEvenWithFullAccess()
    {
        var state = new ComputerUseState(); var tools = new ComputerUseTools(state); var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(true); }); registry.Register(tools);
        var result = await registry.CallAsync("computer_action", JsonSerializer.SerializeToElement(new { windowId = "x", elementId = "x", action = "invoke" }), default);
        Assert.False(result.Ok); Assert.Contains("disabled", result.Error); Assert.Equal(0, approvals);
        Assert.False((await tools.WindowsAsync()).Ok);
    }

    [Theory]
    [InlineData("computer_focus", "")]
    [InlineData("computer_keys", "")]
    [InlineData("computer_action", "double_click")]
    [InlineData("computer_action", "right_click")]
    public void BackgroundProhibitsGlobalInput(string name, string action)
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new ComputerUseState(); state.Configure(true, ComputerInteractionMode.BackgroundOnly);
        var result = new ComputerUseTools(state).ValidateInvocation(name, JsonSerializer.SerializeToElement(new { action }));
        Assert.NotNull(result); Assert.False(result.Ok); Assert.Contains("Background", result.Error);
    }

    [Fact]
    public void ModeChangesRevokeExistingOperationsAndChildOptionsShareSwitch()
    {
        var options = new AgentOptions(); var child = options with { IsSubAgent = true };
        options.ComputerUse.Configure(true, ComputerInteractionMode.BackgroundOnly);
        var old = child.ComputerUse.Snapshot();
        options.ComputerUse.Configure(false, ComputerInteractionMode.ForegroundAllowed);
        Assert.True(old.Token.IsCancellationRequested); Assert.False(child.ComputerUse.Snapshot().Enabled);
    }

    [Fact]
    public async Task IsolatedModeNeverFallsBackToLocalDesktop()
    {
        var state = new ComputerUseState(); state.Configure(true, ComputerInteractionMode.IsolatedSession);
        var result = await new ComputerUseTools(state).WindowsAsync();
        Assert.False(result.Ok); Assert.Contains("requires", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandsPersistOnlyEnableAndAiOwnsModeChoice()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "computer-settings"));
        var state = new ComputerUseState(); var manager = new ComputerUseManager(store, state, new TerminalUi(true, false));
        manager.Load(); Assert.False(state.Snapshot().Enabled);
        manager.Execute(["on"]);
        Assert.Throws<ArgumentException>(() => manager.Execute(["mode", "foreground"]));
        Assert.True(state.TrySelectMode(ComputerInteractionMode.ForegroundAllowed));
        var restored = new ComputerUseState(); new ComputerUseManager(store, restored, new TerminalUi(true, false)).Load();
        Assert.True(restored.Snapshot().Enabled); Assert.Equal(ComputerInteractionMode.BackgroundOnly, restored.Snapshot().Mode);
        manager.Execute(["off"]); Assert.Equal("off", store.GetSetting("computer:enabled"));
        Assert.Contains(CommandCatalog.Builtins, c => c.Text == "/computer");
    }

    [Fact]
    public async Task RealFlaUiBackgroundSetsTextInvokesAndVerifiesResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var fixturePath = Path.GetFullPath($"../../../../../tests/LanAgent.ComputerUseFixture/bin/{configuration}/net8.0-windows/LanAgent.ComputerUseFixture.exe", AppContext.BaseDirectory);
        using var fixture = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false });
        Assert.NotNull(fixture);
        try
        {
            Assert.True(fixture.WaitForInputIdle(10000));
            var state = new ComputerUseState(); state.Configure(true, ComputerInteractionMode.BackgroundOnly);
            var tools = new ComputerUseTools(state);
            var windows = await tools.WindowsAsync(); Assert.True(windows.Ok, windows.Error);
            var list = JsonSerializer.SerializeToElement(windows.Data, ToolResult.JsonOptions);
            var window = Assert.Single(list.EnumerateArray(), w => w.GetProperty("processId").GetInt32() == fixture.Id);
            var id = window.GetProperty("windowId").GetString()!;
            var tree = await tools.TreeAsync(id); Assert.True(tree.Ok, tree.Error);
            var screenshot = await tools.ScreenshotAsync(id); Assert.True(screenshot.Ok, screenshot.Error);
            Assert.Single(screenshot.Images); Assert.Equal("image/png", screenshot.Images[0].MediaType);
            Assert.True(JsonSerializer.SerializeToElement(screenshot.Data).GetProperty("imageAttached").GetBoolean());
            Assert.DoesNotContain("imageBase64", screenshot.ToJson()); Assert.DoesNotContain(screenshot.Images[0].Base64, screenshot.ToJson());
            var nodes = JsonSerializer.SerializeToElement(tree.Data, ToolResult.JsonOptions).GetProperty("elements");
            var edit = Assert.Single(nodes.EnumerateArray(), e => e.GetProperty("role").GetString() == "Edit" && e.GetProperty("name").GetString() == "Fixture input");
            var button = Assert.Single(nodes.EnumerateArray(), e => e.GetProperty("name").GetString() == "Apply");
            var text = await tools.ActionAsync(id, edit.GetProperty("elementId").GetString()!, "type_text", "后台 UIA 测试"); Assert.True(text.Ok, text.Error);
            Assert.Equal("set_value", JsonSerializer.SerializeToElement(text.Data).GetProperty("executed").GetString());
            var invoke = await tools.ActionAsync(id, button.GetProperty("elementId").GetString()!, "click"); Assert.True(invoke.Ok, invoke.Error);
            Assert.Equal("invoke", JsonSerializer.SerializeToElement(invoke.Data).GetProperty("executed").GetString());
            Assert.True((await tools.WaitAsync(id, name: "Applied: 后台 UIA 测试")).Ok);
            var found = await tools.FindAsync(id, name: "Fixture input", role: "Edit"); Assert.True(found.Ok, found.Error);
            Assert.Single(JsonSerializer.SerializeToElement(found.Data).GetProperty("elements").EnumerateArray());
            var page = await tools.TreeAsync(id, maxElements: 2); Assert.True(page.Ok, page.Error);
            var next = JsonSerializer.SerializeToElement(page.Data).GetProperty("nextOffset").GetInt32(); Assert.True(next > 0);
            var page2 = await tools.TreeAsync(id, maxElements: 2, offset: next); Assert.True(page2.Ok, page2.Error);
            Assert.NotEqual(JsonSerializer.SerializeToElement(page.Data).GetProperty("elements")[0].GetProperty("elementId").GetString(), JsonSerializer.SerializeToElement(page2.Data).GetProperty("elements")[0].GetProperty("elementId").GetString());
            var missing = await tools.WaitAsync(id, name: "does-not-exist", timeoutMs: 150); Assert.False(missing.Ok);
            var checkbox = await tools.FindAsync(id, name: "Enable option"); Assert.True(checkbox.Ok, checkbox.Error);
            var checkboxId = Assert.Single(JsonSerializer.SerializeToElement(checkbox.Data).GetProperty("elements").EnumerateArray()).GetProperty("elementId").GetString()!;
            Assert.True((await tools.ActionAsync(id, checkboxId, "toggle")).Ok);
            Assert.True((await tools.WaitAsync(id, name: "Enable option", condition: "on")).Ok);
            var readonlyControl = await tools.FindAsync(id, name: "Readonly input", role: "Edit"); Assert.True(readonlyControl.Ok, readonlyControl.Error);
            var readonlyId = Assert.Single(JsonSerializer.SerializeToElement(readonlyControl.Data).GetProperty("elements").EnumerateArray()).GetProperty("elementId").GetString()!;
            var refused = await tools.ActionAsync(id, readonlyId, "type_text", "must not write"); Assert.False(refused.Ok); Assert.Contains("read-only", refused.Error);
            Assert.True((await tools.WaitAsync(id, name: "Readonly input", role: "Edit", value: "Preserve me")).Ok);
            var after = await tools.TreeAsync(id); Assert.True(after.Ok, after.Error);
            Assert.Contains("Applied: 后台 UIA 测试", JsonSerializer.Serialize(after.Data, new JsonSerializerOptions(ToolResult.JsonOptions) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var stale = await tools.ActionAsync(id, "invalid-stale-id", "invoke"); Assert.False(stale.Ok); Assert.Contains("stale", stale.Error);
            var rename = await tools.FindAsync(id, name: "Rename target"); Assert.True(rename.Ok, rename.Error);
            var renameId = Assert.Single(JsonSerializer.SerializeToElement(rename.Data).GetProperty("elements").EnumerateArray()).GetProperty("elementId").GetString()!;
            Assert.True((await tools.ActionAsync(id, renameId, "click")).Ok);
            Assert.True((await tools.WaitAsync(id, name: "Apply changed")).Ok);
            stale = await tools.ActionAsync(id, button.GetProperty("elementId").GetString()!, "click"); Assert.False(stale.Ok); Assert.Contains("stale", stale.Error);
        }
        finally { BoundedProcess.Kill(fixture); }
    }

    [Fact]
    public async Task AiCanChooseModesButCannotEnableOffSwitchOrFabricateIsolation()
    {
        var state = new ComputerUseState(); var tools = new ComputerUseTools(state);
        Assert.False((await tools.ModeAsync("foreground", "Need physical keyboard")).Ok);
        Assert.False(state.Snapshot().Enabled);
        state.Configure(true, ComputerInteractionMode.BackgroundOnly);
        Assert.True((await tools.ModeAsync("foreground", "User requested current desktop interaction")).Ok);
        Assert.Equal(ComputerInteractionMode.ForegroundAllowed, state.Snapshot().Mode);
        Assert.True((await tools.ModeAsync("background", "Software UI test")).Ok);
        Assert.False((await tools.ModeAsync("isolated", "Uninterrupted keyboard testing")).Ok);
        Assert.Equal(ComputerInteractionMode.BackgroundOnly, state.Snapshot().Mode);
        state.Configure(false, ComputerInteractionMode.BackgroundOnly);
        Assert.False((await tools.ModeAsync("foreground", "Retry")).Ok);
        Assert.False(state.Snapshot().Enabled);
    }

    [Fact]
    public async Task TrayOperationsHonorEnableAndPhysicalInputPolicies()
    {
        var state = new ComputerUseState(); var tools = new ComputerUseTools(state);
        Assert.False((await tools.TrayAsync()).Ok);
        state.Configure(true, ComputerInteractionMode.BackgroundOnly);
        var physical = tools.ValidateInvocation("computer_tray_action", JsonSerializer.SerializeToElement(new { action = "right_click" }));
        Assert.NotNull(physical); Assert.False(physical.Ok);
        var registry = new ToolRegistry(); registry.Register(tools);
        var denied = await registry.CallAsync("computer_tray_action", JsonSerializer.SerializeToElement(new { windowId = "x", elementId = "x", action = "open" }), default);
        Assert.False(denied.Ok); Assert.Contains("approval denied", denied.Error);
        var tray = await tools.TrayAsync(); Assert.True(tray.Ok, tray.Error);
        var data = JsonSerializer.SerializeToElement(tray.Data);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("icons").ValueKind); Assert.Equal(JsonValueKind.Array, data.GetProperty("overflowControls").ValueKind);
        Assert.Contains(tools.Definitions, d => d.Name == "computer_tray_action" && d.Risk == ToolRisk.Execute);
    }

    [Fact]
    public void BuiltinSkillIsInstalledForExistingUsersWithoutReplacingTheirEdits()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "skill-data"));
        store.SetSetting("skills:bundled:v1", "seeded");
        var catalog = new LanAgent.Core.Services.SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "skill-data"), workspace.Root);
        catalog.SeedBundled(); var skill = catalog.Get("computer-use");
        Assert.True(skill.ModelInvocable); Assert.Contains("computer_mode", skill.Instructions); Assert.Contains("computer_wait", skill.Instructions);
        Assert.Contains("type_text", skill.Instructions); Assert.Contains("Only the user", skill.Instructions);
        File.AppendAllText(skill.Path, "\nUser custom instruction."); catalog.SeedBundled();
        Assert.Contains("User custom instruction.", File.ReadAllText(skill.Path));
    }

    [Fact]
    public async Task IsolatedServerAuthenticatesAndRejectsCallersOwnSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var secretFile = Path.Combine(workspace.Root, "token.txt"); await File.WriteAllTextAsync(secretFile, Guid.NewGuid().ToString("N"));
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var state = new ComputerUseState { IsolatedEndpoint = new Uri($"http://127.0.0.1:{port}/"), IsolatedTokenFile = secretFile };
        state.Configure(true, ComputerInteractionMode.IsolatedSession);
        using var server = Process.Start(BoundedProcess.StartInfo(state.WorkerPath, ["--serve", port.ToString(), secretFile])); Assert.NotNull(server);
        try
        {
            using var client = new HttpClient(); System.Net.HttpStatusCode? status = null;
            for (var attempt = 0; attempt < 50 && status == null; attempt++)
            {
                try { using var response = await client.PostAsync(state.IsolatedEndpoint, new StringContent("{}")); status = response.StatusCode; }
                catch (HttpRequestException) { if (server.HasExited) throw new InvalidOperationException(await server.StandardError.ReadToEndAsync()); await Task.Delay(100); }
            }
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, status);
            var result = await new ComputerUseTools(state).WindowsAsync();
            Assert.False(result.Ok); Assert.Contains("caller's desktop session", result.Error);
        }
        finally { BoundedProcess.Kill(server); }
    }

    [Fact]
    public async Task AskAndReadOnlyPoliciesDenyUiChangesBeforeWorkerRuns()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new ComputerUseState(); state.Configure(true, ComputerInteractionMode.BackgroundOnly);
        var tools = new ComputerUseTools(state);
        var args = JsonSerializer.SerializeToElement(new { windowId = "not-a-window", elementId = "not-an-element", action = "invoke" });
        var ask = new ToolRegistry(); ask.Register(tools);
        var denied = await ask.CallAsync("computer_action", args, default); Assert.Contains("approval denied", denied.Error);
        var policy = new LanAgent.Core.Services.SubAgentRolePolicy(() => true, Environment.CurrentDirectory);
        var readOnly = new ToolRegistry((_, _) => ValueTask.FromResult(true), invocationPolicy: policy.Validate); readOnly.Register(tools);
        denied = await readOnly.CallAsync("computer_action", args, default); Assert.Contains("read-only", denied.Error);
        denied = await readOnly.CallAsync("computer_mode", JsonSerializer.SerializeToElement(new { mode = "foreground", reason = "UI test" }), default);
        Assert.Contains("read-only", denied.Error); Assert.Equal(ComputerInteractionMode.BackgroundOnly, state.Snapshot().Mode);
    }

    [Fact]
    public async Task RealHyperlightCodeActCallsRegisteredWindowsBackendAndHonorsOffSwitch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var state = new ComputerUseState(); state.Configure(true, ComputerInteractionMode.BackgroundOnly);
        await using var runtime = await AgentRuntime.CreateAsync(workspace.Options with { ComputerUse = state, FullAccess = true });
        var modeResult = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "r = call_tool('computer_mode', mode='foreground', reason='Check mode selection without sending input')\nif r['ok']:\n    print('selected-foreground')\nelse:\n    print(r['error'])\ns = call_tool('computer_mode', mode='background', reason='Software UI testing')\nif s['ok']:\n    print('selected-background')\nelse:\n    print(s['error'])" }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(modeResult.Ok, modeResult.ToJson()); Assert.Contains("selected-foreground", JsonSerializer.Serialize(modeResult.Data)); Assert.Contains("selected-background", JsonSerializer.Serialize(modeResult.Data));
        Assert.Equal(ComputerInteractionMode.BackgroundOnly, state.Snapshot().Mode);
        var result = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "r = call_tool('computer_windows')\nif r['ok']:\n    print('windows-read', len(r['data']))\nelse:\n    print(r['error'])" }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(result.Ok, result.ToJson());
        Assert.Contains("windows-read", JsonSerializer.Serialize(result.Data, ToolResult.JsonOptions));
        state.Configure(false, ComputerInteractionMode.BackgroundOnly);
        result = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "r = call_tool('computer_windows')\nprint(r['error'])" }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(result.Ok, result.ToJson()); Assert.Contains("disabled", JsonSerializer.Serialize(result.Data, ToolResult.JsonOptions));
    }
}
