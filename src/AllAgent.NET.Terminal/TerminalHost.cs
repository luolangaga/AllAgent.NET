using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Terminal;

/// <summary>
/// Everything one workspace owns for the lifetime of the process: conversations, background jobs, MCP
/// connections, plugins, checkpoints, sandboxes and the main-session runs. Switching the terminal to
/// another workspace keeps the previous host alive, so its runs finish instead of dying with the screen
/// that started them, and switching back finds them where they were.
/// </summary>
internal sealed class TerminalHost : IAsyncDisposable
{
    public required string Workspace { get; init; }
    public required string DataDirectory { get; init; }
    public required DataStore Store { get; init; }
    public required BackgroundTasks Tasks { get; init; }
    public required McpToolService Mcp { get; init; }
    public required SkillCatalog Catalog { get; init; }
    public required WorkspaceCheckpoints Checkpoints { get; init; }
    public required PluginHost Plugins { get; init; }
    public required BrowserUseManager Browser { get; init; }
    public required ComputerUseManager Computer { get; init; }
    public SessionRuns Runs { get; } = new();
    public required ConversationRuntimePool.Lease RuntimeLease { get; init; }
    public ConversationRuntimeFactory RuntimeFactory => RuntimeLease.Factory;
    /// <summary>One runtime per conversation; roles never share execution or disclosure state.</summary>
    public Dictionary<string, AgentRuntime> Runtimes { get; } = new(StringComparer.Ordinal);
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    public bool Busy => Runs.Any || Tasks.Active || RuntimeFactory.HasActiveRuns;

    public async ValueTask DisposeAsync()
    {
        await Runs.StopAllAsync(TimeSpan.FromSeconds(5));
        await Tasks.DisposeAsync();
        await RuntimeLease.DisposeAsync();
        Runtimes.Clear();
        await Mcp.DisposeAsync();
        await Plugins.DisposeAsync();
        Store.Dispose();
    }
}

/// <summary>Process-wide registry of workspace hosts. One host per data directory, reused on every visit.</summary>
internal static class TerminalHosts
{
    /// <summary>Idle workspaces kept ready for a switch back.</summary>
    private const int RetainedWorkspaces = 4;
    private static readonly Dictionary<string, TerminalHost> Hosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static IReadOnlyList<TerminalHost> All { get { lock (Gate) return Hosts.Values.ToArray(); } }

    public static async Task<TerminalHost> GetOrCreateAsync(TerminalArguments args, string dataDirectory, string? sharedConfig, AgentOptions options, TerminalUi ui)
    {
        lock (Gate)
            if (Hosts.TryGetValue(dataDirectory, out var cached)) { cached.LastUsed = DateTime.UtcNow; return cached; }
        var created = await CreateAsync(args, dataDirectory, sharedConfig, options, ui);
        lock (Gate)
        {
            if (Hosts.TryGetValue(dataDirectory, out var existing))
            {
                // Another task won the race; the freshly built host has no runs and is released.
                _ = created.DisposeAsync();
                existing.LastUsed = DateTime.UtcNow;
                return existing;
            }
            Hosts[dataDirectory] = created;
            return created;
        }
    }

    /// <summary>Releases workspaces nobody is using any more once too many are open; busy hosts always stay.</summary>
    public static async Task EvictIdleAsync(string currentDirectory, string? sharedConfig, AgentOptions options, TerminalUi ui)
    {
        var doomed = new List<TerminalHost>();
        lock (Gate)
        {
            var idle = Hosts.Values
                .Where(host => !string.Equals(host.DataDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase) && !host.Busy)
                .OrderByDescending(host => host.LastUsed)
                .Skip(RetainedWorkspaces - 1)
                .ToArray();
            foreach (var host in idle) { Hosts.Remove(host.DataDirectory); doomed.Add(host); }
        }
        foreach (var host in doomed) await host.DisposeAsync();
    }

    public static async Task DisposeAllAsync()
    {
        TerminalHost[] hosts;
        lock (Gate) { hosts = Hosts.Values.ToArray(); Hosts.Clear(); }
        foreach (var host in hosts) await host.DisposeAsync();
    }

    private static async Task<TerminalHost> CreateAsync(TerminalArguments args, string dataDirectory, string? sharedConfig, AgentOptions options, TerminalUi ui)
    {
        DataDirectoryLease.EnsureNoLegacyWriter(dataDirectory);
        ProjectDataDirectory.Register(args.Workspace, dataDirectory);
        var store = new DataStore(dataDirectory, false, sharedConfig);
        try
        {
            if (args.Get("data-dir") == null)
            {
                store.ImportProjectData(args.SharedDirectory, args.Workspace);
                if (OperatingSystem.IsWindows())
                {
                    var folder = Path.GetDirectoryName(dataDirectory)!;
                    File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
                }
            }
            options.BrowserUse.Root = Path.Combine(args.SharedDirectory, "browser-use");
            var browser = new BrowserUseManager(store, options.BrowserUse, ui);
            browser.Load();
            var computer = new ComputerUseManager(store, options.ComputerUse, ui);
            computer.Load();
            var plugins = new PluginHost(args.SharedDirectory);
            plugins.ReserveAuthenticationIds("codex");
            try { plugins.InstallBuiltins(Path.Combine(AppContext.BaseDirectory, "builtin-plugins")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ui.Error("内置插件安装失败：" + ex.Message); }
            var tasks = new BackgroundTasks(dataDirectory, args.BackgroundConcurrency);
            var catalog = new SkillCatalog(store, args.Workspace, args.SharedDirectory);
            catalog.SeedBundled();
            var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(args.Workspace), dataDirectory);
            checkpoints.RecoverRevert(store);
            var mcp = new McpToolService(store, new SkillExecutor());
            var host = new TerminalHost
            {
                Workspace = args.Workspace, DataDirectory = dataDirectory, Store = store, Tasks = tasks,
                RuntimeLease = ConversationRuntimePool.Acquire(dataDirectory),
                Mcp = mcp, Catalog = catalog, Checkpoints = checkpoints, Plugins = plugins, Browser = browser, Computer = computer,
            };
            plugins.ProvideService(store); plugins.ProvideService(catalog); plugins.ProvideService(tasks); plugins.ProvideService(mcp);
            await plugins.LoadEnabledAsync();
            return host;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }
}
