using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Diligence;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class DiligencePluginTests
{
    private static PluginHost CreateHost(string root)
    {
        var directory = Path.Combine(root, "plugins", "diligence");
        Directory.CreateDirectory(directory);
        File.Copy(typeof(DiligencePlugin).Assembly.Location, Path.Combine(directory, "AllAgent.NET.Diligence.dll"));
        File.WriteAllText(Path.Combine(directory, "allagent-plugin.json"),
            """{"sdkVersion":1,"assembly":"AllAgent.NET.Diligence.dll","entryType":"AllAgent.NET.Diligence.DiligencePlugin"}""");
        var host = new PluginHost(root);
        host.Save(new("AllAgent.NET.Diligence", "1.0.0", "diligence"));
        return host;
    }

    private static Task<AgentTurnContext> Turn(PluginHost host, string root, bool child = false) =>
        host.Extensions.ConfigureTurnAsync(new(Guid.NewGuid(), root, child, "task", "original system", ["file_read"], []), default);

    [Fact]
    public async Task ModesPersistAcrossReloadAndDoNotRewriteExistingTurnOrTools()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.Root);
        await host.LoadEnabledAsync();
        Assert.Null(Assert.Single(host.Status).Error);
        var command = Assert.Single(host.Commands);
        var original = await Turn(host, workspace.Root);
        Assert.Equal("original system", original.SystemPrompt);
        Assert.Contains("mode=\"normal\"", original.VolatileSystemPrompt);
        Assert.Contains("标准", await command.Execute([], default));
        Assert.Contains("已保存", await command.Execute(["勤快"], default));
        var diligent = await Turn(host, workspace.Root, child: true);
        Assert.Equal("original system", diligent.SystemPrompt);
        Assert.Contains("mode=\"diligent\"", diligent.VolatileSystemPrompt);
        Assert.Equal(new[] { "file_read" }, diligent.Tools);
        Assert.Empty(diligent.Skills);
        Assert.Contains("mode=\"normal\"", original.VolatileSystemPrompt);
        await host.SetEnabledAsync("AllAgent.NET.Diligence", false);
        var disabled = await Turn(host, workspace.Root);
        Assert.Equal("original system", disabled.SystemPrompt); Assert.Empty(disabled.VolatileSystemPrompt);
        await host.SetEnabledAsync("AllAgent.NET.Diligence", true);
        Assert.Contains("mode=\"diligent\"", (await Turn(host, workspace.Root)).VolatileSystemPrompt);
        command = Assert.Single(host.Commands);
        await command.Execute(["懒"], default);
        Assert.Contains("mode=\"lazy\"", (await Turn(host, workspace.Root)).VolatileSystemPrompt);
        await command.Execute(["off"], default);
        var off = await Turn(host, workspace.Root);
        Assert.Equal("original system", off.SystemPrompt); Assert.Empty(off.VolatileSystemPrompt);
    }

    [Fact]
    public async Task InvalidCancelledAndFailedSavesDoNotChangeMode()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.Root);
        await host.LoadEnabledAsync();
        var command = Assert.Single(host.Commands);
        Assert.Contains("未修改", await command.Execute(["unexpected"], default));
        Assert.Contains("未修改", await command.Execute(["lazy", "extra"], default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.Execute(["lazy"], cancelled.Token));
        var path = Path.Combine(workspace.Root, "plugins", "diligence", "data", "effort.json");
        Directory.CreateDirectory(path); // force the atomic save to fail
        var failure = await Record.ExceptionAsync(() => command.Execute(["lazy"], default));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Contains("mode=\"normal\"", (await Turn(host, workspace.Root)).VolatileSystemPrompt);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
