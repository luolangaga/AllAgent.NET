using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

[Collection("Terminal interaction")]
public sealed class BrowserTransportTests
{
    [Fact]
    public async Task RealMontyLoadsSkillWithNameAndDictionaryThenControlsWebView2()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        var options = workspace.Options with
        {
            SandboxMode = SandboxMode.Monty, Skills = catalog, Permission = PermissionMode.Full,
            SharedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET"),
            BrowserUse = new BrowserUseState { Root = Path.Combine(workspace.Root, "browser") }
        };
        var runtime = await AgentRuntime.CreateAsync(options);
        try
        {
        var loaded = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new
        { code = "print(call_tool('skill_read', name='browser-use')['ok'])\nprint(call_tool('skill_read', {'name':'browser-use'})['ok'])" }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(loaded.Ok, loaded.Error); Assert.Contains("True", loaded.ToJson());
        var data = JsonSerializer.SerializeToElement(loaded.Data, ToolResult.JsonOptions);
        Assert.Equal(0, data.GetProperty("hostCallSummary").GetProperty("failed").GetInt32());
        var browser = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new
        { code = "import json\nprint(call_tool('browser', operation='open')['ok'])\nprint(call_tool('browser', operation='evaluate', arguments_json=json.dumps({'script': 'document.title = \"Browser IPC proof\"; Promise.resolve(document.title)'})))" }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(browser.Ok, browser.Error); Assert.True(browser.ToJson().Contains("Browser IPC proof", StringComparison.Ordinal), browser.ToJson());
        data = JsonSerializer.SerializeToElement(browser.Data, ToolResult.JsonOptions);
        Assert.Equal(0, data.GetProperty("hostCallSummary").GetProperty("failed").GetInt32());
        }
        finally
        {
            await runtime.DisposeAsync();
            var profile = Path.GetFullPath(options.BrowserUse.Root);
            Assert.StartsWith(Path.GetFullPath(workspace.Root) + Path.DirectorySeparatorChar, profile);
            for (var i = 0; i < 30 && Directory.Exists(profile); i++)
                try { Directory.Delete(profile, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}
