using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LanAgent.Core.Bridge;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace LanAgent.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static EmbeddedWebServer? _webServer = null;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Seed built-in skills on startup
        var store = Services.GetRequiredService<DataStore>();
        SeedBuiltinSkills(store);

        // Register all bridge handlers immediately
        var registrar = Services.GetRequiredService<BridgeHandlerRegistrar>();
        registrar.RegisterAll();

        // Connect enabled MCP servers
        var mcpService = Services.GetRequiredService<McpToolService>();
        _ = ConnectMcpServers(mcpService, store);

#if DEBUG
        var startUrl = "http://127.0.0.1:5173";
#else
        _webServer = new EmbeddedWebServer();
        _webServer.StartAsync().GetAwaiter().GetResult();
        var startUrl = $"http://127.0.0.1:{_webServer.Port}";
#endif

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Services, startUrl);
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try { Services.GetService<AgentService>()?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* ignore */ }
        // Ensure DataStore is disposed (flushes temp files, releases locks)
        try { (Services.GetService(typeof(DataStore)) as IDisposable)?.Dispose(); }
        catch { /* ignore */ }

        try { (Services.GetService(typeof(McpToolService)) as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* ignore */ }

        _webServer?.Dispose();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
        // In a production app you might log to a crash file here
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DataStore>();
        services.AddSingleton<AiProviderFactory>();
        services.AddSingleton<SkillExecutor>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton(sp => new AgentService(sp.GetRequiredService<DataStore>(), sp.GetRequiredService<AiProviderFactory>(),
            new AgentOptions
            {
                BackgroundTasks = new BackgroundTasks(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent", "desktop-tasks")),
                Approver = AgentApprovalDialog.ApproveAsync,
                InstallationApprover = AgentApprovalDialog.InstallAsync,
                AuditPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent", "audit.jsonl")
            }, sp.GetRequiredService<McpToolService>()));
        services.AddSingleton<BridgeDispatcher>();
        services.AddSingleton<BridgeEventPusher>();
        services.AddSingleton<BridgeHandlerRegistrar>();
    }

    private static void SeedBuiltinSkills(DataStore store)
    {
        var existingNames = store.GetSkillNames();
        var toAdd = GetBuiltinSkills().Where(s => !existingNames.Contains(s.Name)).ToList();
        if (toAdd.Count > 0)
            store.AddSkills(toAdd);
        var legacyShell = store.Skills.FirstOrDefault(s => s.Name == "shell_exec" && s.IsEnabled);
        if (legacyShell != null) { legacyShell.IsEnabled = false; store.UpdateSkill(legacyShell); }
    }

    private static async Task ConnectMcpServers(McpToolService mcpService, DataStore store)
    {
        foreach (var config in store.McpServerConfigs.Where(c => c.IsEnabled))
        {
            try
            {
                await mcpService.ConnectServerAsync(config);
            }
            catch { /* skip unavailable servers at startup */ }
        }
    }

    private static Skill[] GetBuiltinSkills() => new SystemTools(
        new WorkspacePolicy(Environment.CurrentDirectory), new RuntimeEnvironment(null, null, Array.Empty<string>()),
        new System.Collections.Generic.Dictionary<string, string>()).Definitions.Select(tool => new Skill
        {
            Name = tool.Name,
            Description = tool.Description,
            ParametersJson = tool.Parameters.GetRawText(),
            Type = SkillType.Builtin,
            IsEnabled = true
        }).ToArray();
}
