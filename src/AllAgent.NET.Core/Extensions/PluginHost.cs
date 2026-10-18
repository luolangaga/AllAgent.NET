using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Extensions;

public sealed record InstalledPlugin(string PackageId, string Version, string Directory, bool Enabled = true);
public sealed record PluginStatus(string Id, string Name, string PackageId, string Version, bool Enabled, string? Error, IReadOnlyList<string>? Tools = null, IReadOnlyList<string>? Commands = null);

public sealed class PluginHost : IAsyncDisposable
{
    private sealed record Loaded(IAllAgentPlugin Plugin, PluginContext Context, PluginLoadContext Loader, InstalledPlugin Package);
    private readonly List<Loaded> _loaded = [];
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly object _sync = new();
    private IReadOnlyList<InstalledPlugin>? _installedSnapshot;
    private (long Length, DateTime Modified)? _manifestStamp;
    private long _manifestChecked;
    private readonly HashSet<string> _disabledHooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedAuthenticationIds = new(StringComparer.OrdinalIgnoreCase);
    public void ReserveAuthenticationIds(params string[] ids) { lock (_sync) { if (_loaded.Count > 0) throw new InvalidOperationException("必须在加载插件前保留登录 ID。"); foreach (var id in ids) _reservedAuthenticationIds.Add(id); } }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _services = new();
    public void ProvideService<T>(T service) where T : class => _services[typeof(T)] = service;
    public PluginHost(string dataDirectory) { _root = Path.Combine(Path.GetFullPath(dataDirectory), "plugins"); Directory.CreateDirectory(_root); if (File.Exists(Path.Combine(_root, "disabled-hooks.json"))) foreach (var name in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_root, "disabled-hooks.json"))) ?? []) _disabledHooks.Add(name); Hooks = new(() => HookSnapshot()); }
    public HookPipeline Hooks { get; }
    public AgentExtensionPipeline Extensions => new(
        () => { lock (_sync) return _loaded.SelectMany(l => l.Context.TurnExtensions).ToArray(); },
        () => { lock (_sync) return _loaded.SelectMany(l => l.Context.RequestExtensions).ToArray(); });
    private string ManifestPath => Path.Combine(_root, "installed.json");
    public IReadOnlyList<InstalledPlugin> Installed
    {
        get
        {
            lock (_sync)
            {
                var now = Environment.TickCount64;
                if (_installedSnapshot != null && now - _manifestChecked < 1000) return _installedSnapshot;
                var file = new FileInfo(ManifestPath);
                (long Length, DateTime Modified)? stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
                if (_installedSnapshot == null || stamp != _manifestStamp)
                {
                    _installedSnapshot = file.Exists ? JsonSerializer.Deserialize<InstalledPlugin[]>(File.ReadAllText(ManifestPath)) ?? [] : [];
                    _manifestStamp = stamp;
                }
                _manifestChecked = now;
                return _installedSnapshot;
            }
        }
    }
    public IReadOnlyList<PluginStatus> Status => Installed.Select(p => { lock (_sync) { var loaded = _loaded.FirstOrDefault(l => l.Package.PackageId.Equals(p.PackageId, StringComparison.OrdinalIgnoreCase)); return new PluginStatus(loaded?.Plugin.Id ?? p.PackageId, loaded?.Plugin.Name ?? p.PackageId, p.PackageId, p.Version, p.Enabled, _errors.GetValueOrDefault(p.PackageId),
        loaded == null ? [] : loaded.Context.Tools.SelectMany(t => t.Definitions).Select(d => d.Name).ToArray(),
        loaded == null ? [] : loaded.Context.Commands.Select(c => c.Name).ToArray()); } }).ToArray();
    /// <summary>Every loaded plugin tool with the package it belongs to, for source attribution in tool catalogs.</summary>
    public IReadOnlyList<(string PackageId, AgentToolDescriptor Tool)> PluginTools
    {
        get { lock (_sync) return _loaded.SelectMany(l => l.Context.Tools.SelectMany(t => t.Definitions).Select(d => (l.Package.PackageId, d))).ToArray(); }
    }
    public IReadOnlyList<IAiProvider> Providers { get { lock (_sync) return _loaded.SelectMany(l => l.Context.Providers).ToArray(); } }
    public IReadOnlyList<ModelFooterProvider> FooterProviders { get { lock (_sync) return _loaded.SelectMany(l => l.Context.FooterProviders).ToArray(); } }
    public IReadOnlyList<PluginCommand> Commands { get { lock (_sync) return _loaded.SelectMany(l => l.Context.Commands).ToArray(); } }
    public IReadOnlyList<IAuthenticationProvider> Authentication { get { lock (_sync) return _loaded.SelectMany(l => l.Context.Authentication).ToArray(); } }
    public IReadOnlyList<(string Name, bool Enabled)> HookStatus { get { lock (_sync) return _loaded.SelectMany(l => l.Context.Hooks).Select(h => (h.Name, !_disabledHooks.Contains(h.Name))).ToArray(); } }
    public void SetHookEnabled(string name, bool enabled)
    { lock (_sync) { if (!HookStatus.Any(h => h.Name == name)) throw new ArgumentException("Hook 不存在。"); if (enabled) _disabledHooks.Remove(name); else _disabledHooks.Add(name); File.WriteAllText(Path.Combine(_root, "disabled-hooks.json"), JsonSerializer.Serialize(_disabledHooks)); } }
    private IReadOnlyList<AgentHook> HookSnapshot() { lock (_sync) return _loaded.SelectMany(l => l.Context.Hooks).Where(h => !_disabledHooks.Contains(h.Name)).ToArray(); }
    public IAgentToolSet ToolSet() { lock (_sync) return new PluginToolSet(_loaded.SelectMany(l => l.Context.Tools).ToArray()); }
    public void Save(InstalledPlugin package)
    {
        var items = Installed.Where(p => !p.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)).Append(package).ToArray();
        var temp = ManifestPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, ManifestPath, true);
        lock (_sync) _installedSnapshot = null;
    }
    public async Task LoadEnabledAsync(CancellationToken ct = default)
    {
        foreach (var package in Installed.Where(p => p.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) if (_loaded.Any(l => l.Package.PackageId == package.PackageId)) continue;
            try { Load(package); _errors.Remove(package.PackageId); }
            catch (Exception ex) { _errors[package.PackageId] = ex.GetBaseException().Message; }
        }
        await Task.CompletedTask;
    }
    /// <summary>Install plugins shipped next to the host once; an existing installation of the same package is kept.</summary>
    public void InstallBuiltins(string builtinRoot)
    {
        if (!Directory.Exists(builtinRoot)) return;
        foreach (var source in Directory.GetDirectories(builtinRoot))
        {
            var manifestPath = Path.Combine(source, "allagent-plugin.json");
            if (!File.Exists(manifestPath)) continue;
            var packageId = Path.GetFileName(source);
            if (Installed.Any(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))) continue;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("内置插件清单无效：" + packageId);
            var assembly = Path.Combine(source, manifest.Assembly);
            var version = File.Exists(assembly) ? AssemblyName.GetAssemblyName(assembly).Version?.ToString(3) ?? "1.0.0" : "1.0.0";
            var folder = packageId.ToLowerInvariant() + "-" + version + "-builtin";
            var destination = Path.Combine(_root, folder);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            CopyDirectory(source, destination);
            Save(new(packageId, version, folder));
        }
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private void Load(InstalledPlugin package)
    {
        var directory = Path.GetFullPath(Path.Combine(_root, package.Directory));
        if (!directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("插件目录越界。");
        // Older installs shipped lanagent-plugin.json against the pre-rename LanAgent assemblies.
        if (!File.Exists(Path.Combine(directory, "allagent-plugin.json")) && File.Exists(Path.Combine(directory, "lanagent-plugin.json")))
            throw new InvalidDataException("这是旧版 LanAgent 插件包，与当前 AllAgent.NET 宿主不兼容；请重新构建并安装新版插件包（/plugins import）。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(directory, "allagent-plugin.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("缺少插件清单。");
        if (manifest.SdkVersion != 1 || Path.GetFileName(manifest.Assembly) != manifest.Assembly || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件 SDK 版本或入口程序集无效。");
        var path = Path.Combine(directory, manifest.Assembly); var loader = new PluginLoadContext(path);
        IAllAgentPlugin? plugin = null;
        try
        {
            var type = loader.LoadEntry(path).GetType(manifest.EntryType, true)!;
            plugin = Activator.CreateInstance(type) as IAllAgentPlugin ?? throw new InvalidDataException("入口类型必须实现 IAllAgentPlugin。");
            var context = new PluginContext(Path.Combine(directory, "data"), _services); Directory.CreateDirectory(context.DataDirectory); plugin.Register(context);
            lock (_sync)
            {
                if (_loaded.Any(l => l.Plugin.Id == plugin.Id)) throw new InvalidOperationException("插件 ID 冲突。");
                var allTools = _loaded.SelectMany(l => l.Context.Tools).Concat(context.Tools).SelectMany(t => t.Definitions).Select(t => t.Name).ToArray();
                if (allTools.Distinct(StringComparer.Ordinal).Count() != allTools.Length) throw new InvalidOperationException("插件工具名称冲突。");
                var reserved = new[] { "file_read", "file_write", "file_edit", "list_directory", "run_shell", "ask_user", "subagent", "cancel_agent", "execute_code" };
                if (allTools.Any(n => reserved.Contains(n) || n.StartsWith("mcp_", StringComparison.Ordinal) || n.StartsWith("skill_", StringComparison.Ordinal))) throw new InvalidOperationException("插件工具与宿主保留名称冲突；请加插件前缀。");
                ValidateIds(_loaded.SelectMany(l => l.Context.Commands).Select(c => c.Name).Concat(context.Commands.Select(c => c.Name)));
                ValidateIds(_loaded.SelectMany(l => l.Context.Providers).Select(c => c.ProviderId).Concat(context.Providers.Select(c => c.ProviderId)));
                if (context.Providers.Any(p => new[] { "openai", "anthropic", "gemini", "ollama", "responses" }.Contains(p.ProviderId))) throw new InvalidOperationException("插件不能替换内置模型提供商。");
                ValidateIds(_loaded.SelectMany(l => l.Context.Authentication).Select(c => c.Id).Concat(context.Authentication.Select(c => c.Id)));
                if (context.Authentication.Any(a => _reservedAuthenticationIds.Contains(a.Id))) throw new InvalidOperationException("认证插件与内置登录 ID 冲突；请使用独立的插件 ID。");
                ValidateIds(_loaded.SelectMany(l => l.Context.Hooks).Select(c => c.Name).Concat(context.Hooks.Select(c => c.Name)));
                ValidateIds(_loaded.SelectMany(l => l.Context.TurnExtensions).Select(c => c.Name).Concat(context.TurnExtensions.Select(c => c.Name)));
                ValidateIds(_loaded.SelectMany(l => l.Context.RequestExtensions).Select(c => c.Name).Concat(context.RequestExtensions.Select(c => c.Name)));
                _loaded.Add(new(plugin, context, loader, package));
            }
        }
        catch { if (plugin != null) plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); loader.Unload(); throw; }
    }
    private static void ValidateIds(IEnumerable<string> ids)
    { var values = ids.ToArray(); if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length) throw new InvalidOperationException("插件扩展 ID 为空或冲突。"); }
    public async Task SetEnabledAsync(string packageId, bool enabled)
    {
        var package = Installed.Single(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        // Host changes are invoked only when no agent is executing; no stale tool invocation can race unload.
        List<Loaded> removed;
        lock (_sync) { removed = _loaded.Where(l => l.Package.PackageId == package.PackageId).ToList(); _loaded.RemoveAll(l => l.Package.PackageId == package.PackageId); }
        foreach (var loaded in removed) { try { await loaded.Plugin.DisposeAsync(); } finally { loaded.Loader.Unload(); } }
        Save(package with { Enabled = enabled }); if (enabled) await LoadEnabledAsync();
    }
    public async Task RemoveAsync(string packageId)
    {
        await SetEnabledAsync(packageId, false);
        // Keep files recoverable; removing the manifest entry makes the package unavailable to the host.
        File.WriteAllText(ManifestPath + ".tmp", JsonSerializer.Serialize(Installed.Where(p => !p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))); File.Move(ManifestPath + ".tmp", ManifestPath, true);
        lock (_sync) _installedSnapshot = null;
    }
    public async ValueTask DisposeAsync()
    { foreach (var loaded in _loaded) { try { await loaded.Plugin.DisposeAsync(); } finally { loaded.Loader.Unload(); } } _loaded.Clear(); }
}

public sealed record PluginManifest(string Assembly, string EntryType, int SdkVersion = 1);

internal sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(path);
    private readonly string _directory = Path.GetDirectoryName(path)!;
    public Assembly LoadEntry(string file) { using var stream = File.OpenRead(file); return LoadFromStream(stream); }
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IAllAgentPlugin).Assembly.GetName().Name) return typeof(IAllAgentPlugin).Assembly;
        var resolved = _resolver.ResolveAssemblyToPath(name);
        if (resolved == null && !string.IsNullOrEmpty(name.CultureName) && File.Exists(Path.Combine(_directory, name.CultureName, name.Name + ".dll"))) resolved = Path.Combine(_directory, name.CultureName, name.Name + ".dll");
        if (resolved == null && File.Exists(Path.Combine(_directory, name.Name + ".dll"))) resolved = Path.Combine(_directory, name.Name + ".dll");
        return resolved == null ? null : LoadEntry(resolved);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    { var resolved = _resolver.ResolveUnmanagedDllToPath(name); return resolved == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(resolved); }
}

public sealed class PluginToolSet(IReadOnlyList<IAgentToolSet> sets) : IAgentToolSet
{
    public IReadOnlyList<AgentToolDescriptor> Definitions => sets.SelectMany(s => s.Definitions).ToArray();
    public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => sets.Single(s => s.Definitions.Any(d => d.Name == name)).InvokeAsync(name, arguments, ct);
}
