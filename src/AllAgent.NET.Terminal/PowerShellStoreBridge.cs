using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Terminal;

// PowerShell 能力商店 payloads: the browser lists the verified module catalog, searches PowerShell
// Gallery, installs a module with its dependencies and reads back the exported commands.
public sealed record PowerShellModuleInfo(string Name, string Version, int CommandCount, string? Error);
public sealed record PowerShellStoreStatus(bool WorkerAvailable, string WorkerPath, string Root, IReadOnlyList<PowerShellModuleInfo> Modules);
public sealed record PowerShellSearchParams(string Query);
public sealed record PowerShellPackageInfo(string Name, string Version, string Author, string Description);
public sealed record PowerShellInstallParams(string Name, string? Version = null);
public sealed record PowerShellCommandsParams(string Name, string Version = "", int Limit = 100);
public sealed record PowerShellCommandPage(string Name, string Version, int Total, IReadOnlyList<string> Commands, string? Error);

/// <summary>
/// The browser's PowerShell 能力商店, the same store the terminal's /powershell panel drives: modules
/// come from PowerShell Gallery into the shared AllAgent directory, are import-validated by the bundled
/// SDK worker and only then count as capabilities. The directory is shared with the terminal, so a
/// module installed from either surface shows up in both.
/// </summary>
internal sealed class PowerShellStoreBridge(PowerShellModuleStore store)
{
    private static readonly Regex ModuleName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex ModuleVersion = new(@"^\d+\.\d+(?:\.\d+){0,2}$", RegexOptions.Compiled);

    public void Register(BridgeDispatcher dispatcher)
    {
        dispatcher.Register<PowerShellStoreStatus>("powershell.status", _ => Task.FromResult(Status()));
        // Re-imports every module in the worker when a catalog record has to be trusted again.
        dispatcher.Register<PowerShellStoreStatus>("powershell.refresh", async (ct) =>
        {
            await store.ExecuteAsync("list", ct: ct);
            return Status();
        });
        dispatcher.Register<PowerShellSearchParams, IReadOnlyList<PowerShellPackageInfo>>("powershell.search", SearchAsync);
        dispatcher.Register<PowerShellInstallParams, PowerShellStoreStatus>("powershell.install", InstallAsync);
        dispatcher.Register<PowerShellCommandsParams, PowerShellCommandPage>("powershell.commands", CommandsAsync);
    }

    private PowerShellStoreStatus Status()
    {
        // Another surface (the terminal, a second tab) may have rewritten the catalog since the last read.
        store.LoadCatalog();
        using var catalog = JsonDocument.Parse(store.Summary);
        var modules = catalog.RootElement.EnumerateArray().Select(item => new PowerShellModuleInfo(
            Text(item, "name"),
            Text(item, "version"),
            item.TryGetProperty("commandCount", out var count) ? count.GetInt32() : 0,
            Text(item, "error") is { Length: > 0 } error ? error : null)).ToArray();
        return new PowerShellStoreStatus(File.Exists(store.WorkerPath), store.WorkerPath, store.Root, modules);
    }

    private async Task<IReadOnlyList<PowerShellPackageInfo>> SearchAsync(PowerShellSearchParams parameters, CancellationToken ct)
    {
        var query = (parameters.Query ?? string.Empty).Trim();
        if (query.Length == 0) throw new ArgumentException("请输入要搜索的模块关键词。");
        if (!Regex.IsMatch(query, "^[A-Za-z0-9._-]{1,64}$")) throw new ArgumentException("关键词只能包含字母、数字、点、连字符和下划线。");
        // Search goes straight to the Gallery API and does not need the bundled SDK worker;
        // only installs and module import validation do.
        // The Gallery returns at most 30 matches; the page lists them and installs by exact version.
        var json = await store.ExecuteAsync("search", "*" + query + "*", ct: ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => new PowerShellPackageInfo(
            Text(item, "Name"), Text(item, "Version"), Text(item, "Author"), Text(item, "Description"))).ToArray();
    }

    private async Task<PowerShellStoreStatus> InstallAsync(PowerShellInstallParams parameters, CancellationToken ct)
    {
        var name = (parameters.Name ?? string.Empty).Trim();
        if (!ModuleName.IsMatch(name)) throw new ArgumentException("模块名称无效：请使用字母、数字、点、连字符或下划线，并以字母或数字开头。");
        var version = (parameters.Version ?? string.Empty).Trim();
        if (version.Length > 0 && !ModuleVersion.IsMatch(version)) throw new ArgumentException("版本格式无效：请填写完整稳定版本，例如 5.7.1。");
        EnsureWorker();
        try
        {
            await store.ExecuteAsync("install", name, version.Length == 0 ? null : version, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Module downloaded, but import validation failed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("模块已下载，但导入验证失败：" + name + " 无法在当前 PowerShell 7 运行时中导入，或缺少依赖。刷新清单可以查看具体的导入错误。");
        }
        return Status();
    }

    private Task<PowerShellCommandPage> CommandsAsync(PowerShellCommandsParams parameters, CancellationToken ct)
    {
        var name = (parameters.Name ?? string.Empty).Trim();
        var version = (parameters.Version ?? string.Empty).Trim();
        var limit = Math.Clamp(parameters.Limit, 1, 100);
        store.LoadCatalog();
        using var page = JsonDocument.Parse(JsonSerializer.Serialize(store.Commands(name, skip: 0, limit)));
        foreach (var module in page.RootElement.EnumerateArray())
        {
            if (version.Length > 0 && !string.Equals(Text(module, "version"), version, StringComparison.OrdinalIgnoreCase)) continue;
            var error = Text(module, "error");
            return Task.FromResult(new PowerShellCommandPage(
                Text(module, "name"),
                Text(module, "version"),
                module.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                module.TryGetProperty("commands", out var commands) ? commands.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [],
                error.Length == 0 ? null : error));
        }
        throw new InvalidOperationException("模块不在能力清单中：" + name + "。请先安装它，或刷新清单。");
    }

    private void EnsureWorker()
    {
        if (!File.Exists(store.WorkerPath))
            throw new InvalidOperationException("内嵌 PowerShell SDK worker 缺失：" + store.WorkerPath + "。请重新安装 AllAgent；不需要安装系统 PowerShell。");
    }

    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
