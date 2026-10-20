using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Extensions;

public sealed record InstalledPlugin(string PackageId, string Version, string Directory, bool Enabled = true, bool WebEnabled = true);
public sealed record PluginStatus(string Id, string Name, string PackageId, string Version, bool Enabled, string? Error, IReadOnlyList<string>? Tools = null, IReadOnlyList<string>? Commands = null, PluginWebStatus? Web = null);

public sealed class PluginHost : IAsyncDisposable
{
    private sealed record Loaded(IAllAgentPlugin Plugin, PluginContext Context, PluginLoadContext Loader, InstalledPlugin Package);
    private sealed record WebLoaded(PluginWebManifest Manifest, PluginWebContributions Contributions, IReadOnlyList<PluginWebBridgeHandler> Bridges, string? Error);
    private readonly List<Loaded> _loaded = [];
    private readonly Dictionary<string, WebLoaded> _web = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Fan-out for plugin backend → browser frames events; the web UI host rebroadcasts them.</summary>
    public PluginWebEvents WebEvents { get; } = new();
    /// <summary>Raised after any change to the plugin set or their web enablement, so hosts can
    /// re-sync bridge bindings and tell connected pages to reload contributions.</summary>
    public event Action? Changed;
    private void RaiseChanged() => Changed?.Invoke();
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly PluginSettingsStore _settings;
    private readonly object _sync = new();
    private IReadOnlyList<InstalledPlugin>? _installedSnapshot;
    private (long Length, DateTime Modified)? _manifestStamp;
    private long _manifestChecked;
    private readonly HashSet<string> _disabledHooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedAuthenticationIds = new(StringComparer.OrdinalIgnoreCase);
    public void ReserveAuthenticationIds(params string[] ids) { lock (_sync) { if (_loaded.Count > 0) throw new InvalidOperationException("必须在加载插件前保留登录 ID。"); foreach (var id in ids) _reservedAuthenticationIds.Add(id); } }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _services = new();
    public void ProvideService<T>(T service) where T : class => _services[typeof(T)] = service;
    public PluginHost(string dataDirectory) { _root = Path.Combine(Path.GetFullPath(dataDirectory), "plugins"); Directory.CreateDirectory(_root); _settings = new PluginSettingsStore(_root); if (File.Exists(Path.Combine(_root, "disabled-hooks.json"))) foreach (var name in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_root, "disabled-hooks.json"))) ?? []) _disabledHooks.Add(name); Hooks = new(() => HookSnapshot()); }
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
    public IReadOnlyList<PluginStatus> Status => Installed.Select(p => { lock (_sync) { var loaded = _loaded.FirstOrDefault(l => l.Package.PackageId.Equals(p.PackageId, StringComparison.OrdinalIgnoreCase)); _web.TryGetValue(p.PackageId, out var web); return new PluginStatus(loaded?.Plugin.Id ?? p.PackageId, loaded?.Plugin.Name ?? p.PackageId, p.PackageId, p.Version, p.Enabled, _errors.GetValueOrDefault(p.PackageId),
        loaded == null ? [] : loaded.Context.Tools.SelectMany(t => t.Definitions).Select(d => d.Name).ToArray(),
        loaded == null ? [] : loaded.Context.Commands.Select(c => c.Name).ToArray(),
        web == null ? null : new PluginWebStatus(p.WebEnabled, web.Contributions.Pages?.Count ?? 0, web.Contributions.Panels?.Count ?? 0, web.Contributions.ToolCards?.Count ?? 0, web.Manifest.Capabilities ?? [], web.Error) { Settings = web.Contributions.Settings?.Count ?? 0 }); } }).ToArray();
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
    public IAgentToolSet WorkspaceIndependentToolSet() { lock (_sync) return new PluginToolSet(_loaded.SelectMany(l => l.Context.WorkspaceIndependentTools).ToArray()); }

    // ── Web extensions ────────────────────────────────────────────────────────

    /// <summary>Bridge methods contributed by plugins, namespaced plugin.&lt;packageId&gt;.&lt;name&gt;.
    /// Only enabled, web-enabled plugins with a valid web manifest are exposed.</summary>
    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> WebBridgeHandlers
    {
        get
        {
            lock (_sync)
            {
                var active = Installed.ToDictionary(p => p.PackageId, p => p.Enabled && p.WebEnabled, StringComparer.OrdinalIgnoreCase);
                var handlers = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(StringComparer.Ordinal);
                foreach (var (packageId, web) in _web)
                {
                    if (web.Error != null || !active.GetValueOrDefault(packageId)) continue;
                    foreach (var bridge in web.Bridges)
                    {
                        var handler = bridge.Handler;
                        handlers["plugin." + packageId.ToLowerInvariant() + "." + bridge.Name] = async (parameters, ct) =>
                        {
                            var result = await handler(parameters.ValueKind == JsonValueKind.Undefined ? null : parameters, ct);
                            return result.HasValue ? (object)result.Value : null;
                        };
                    }
                }
                return handlers;
            }
        }
    }

    /// <summary>Contribution snapshot for the browser: one entry per installed package that ships a
    /// web block, whether or not it is currently loaded, so the plugins page can show and toggle it.</summary>
    public PluginWebSnapshot WebSnapshot(string? assetBaseUrl)
    {
        var items = new List<PluginWebInfo>();
        foreach (var package in Installed)
        {
            WebLoaded? web; string? loadError;
            lock (_sync)
            {
                _web.TryGetValue(package.PackageId, out web);
                loadError = _errors.GetValueOrDefault(package.PackageId);
                if (web == null && !package.Enabled) web = TryReadWebManifest(package);
            }
            if (web == null) continue;
            var loaded = Status.FirstOrDefault(status => status.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
            items.Add(new PluginWebInfo(
                package.PackageId,
                loaded?.Id ?? package.PackageId,
                loaded?.Name ?? package.PackageId,
                package.Version,
                package.Enabled,
                package.WebEnabled,
                package.Enabled && package.WebEnabled && web.Error == null,
                web.Manifest.Capabilities ?? [],
                web.Contributions,
                ResolveDevUrl(web.Manifest.DevUrl),
                web.Error ?? loadError));
        }
        return new PluginWebSnapshot(1, assetBaseUrl, items);
    }

    /// <summary>Soft kill switch for one plugin's web UI; persisted in installed.json. Raise-through:
    /// any change re-pushes contributions to connected pages.</summary>
    public void SetWebEnabled(string packageId, bool enabled)
    {
        var package = Installed.Single(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        Save(package with { WebEnabled = enabled });
    }

    /// <summary>Values declared by a plugin's web settings sections, for the host settings page.</summary>
    public IReadOnlyDictionary<string, string> SettingsSnapshot(string packageId)
    {
        var declared = DeclaredSettings(packageId).ToHashSet(StringComparer.Ordinal);
        return _settings.GetAll(packageId)
            .Where(pair => declared.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Setting keys declared by the plugin manifest; the host refuses undeclared writes.</summary>
    public IReadOnlyCollection<string> DeclaredSettings(string packageId)
    {
        lock (_sync)
        {
            if (!_web.TryGetValue(packageId, out var web) || web.Error != null) return [];
            return web.Contributions.Settings?
                .SelectMany(section => section.Fields ?? [])
                .Select(field => field.Key)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
        }
    }

    /// <summary>Host-side write path: only keys the manifest declared are accepted.</summary>
    public bool SetSetting(string packageId, string key, string? value)
    {
        if (!DeclaredSettings(packageId).Contains(key ?? "", StringComparer.Ordinal))
            throw new ArgumentException("插件未声明该设置项。", nameof(key));
        _settings.Set(packageId, key!, value);
        RaiseChanged();
        return true;
    }

    /// <summary>Install directory of an active web plugin, or null when its assets must not be served.</summary>
    public string? WebAssetRoot(string packageId)
    {
        lock (_sync)
        {
            var package = Installed.FirstOrDefault(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (package == null || !package.Enabled || !package.WebEnabled) return null;
            if (!_web.TryGetValue(package.PackageId, out var web) || web.Error != null) return null;
            var directory = Path.GetFullPath(Path.Combine(_root, package.Directory));
            return directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? directory : null;
        }
    }

    private WebLoaded? TryReadWebManifest(InstalledPlugin package)
    {
        try
        {
            var directory = Path.GetFullPath(Path.Combine(_root, package.Directory));
            if (!directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var manifestPath = Path.Combine(directory, "allagent-plugin.json");
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return manifest?.Web == null ? null : new WebLoaded(manifest.Web, PluginWebContributions.Empty, [], null);
        }
        catch { return null; }
    }

    /// <summary>Validates the declared web block; failures disable only the web part of the plugin.</summary>
    private static WebLoaded? ValidateWeb(string directory, PluginWebManifest? manifest, PluginContext context)
    {
        if (manifest == null) return null;
        if (manifest.SdkVersion > 1) return new(manifest, PluginWebContributions.Empty, [], "插件 Web 扩展需要的宿主版本更高（web.sdkVersion=" + manifest.SdkVersion + "）。");
        try
        {
            var webRoot = Path.GetFullPath(Path.Combine(directory, "web"));
            if (manifest.Entry != null) PluginWebValidation.ResolveEntry(manifest.Entry, directory, webRoot, "默认页");
            var contributions = PluginWebValidation.Normalize(manifest.Contributes, directory, webRoot);
            var bridges = context.WebBridges;
            PluginWebValidation.ValidateIds(bridges.Select(bridge => bridge.Name), "Web 桥方法");
            return new(manifest with { DevUrl = ResolveDevUrl(manifest.DevUrl) }, contributions, bridges, null);
        }
        catch (Exception ex) { return new(manifest, PluginWebContributions.Empty, [], ex.GetBaseException().Message); }
    }

    /// <summary>Dev-server override for local plugin development. Honored only when the host runs
    /// with ALLAGENT_PLUGIN_DEV=1 and the URL is loopback HTTP.</summary>
    private static string? ResolveDevUrl(string? value)
        => Environment.GetEnvironmentVariable("ALLAGENT_PLUGIN_DEV") == "1" && value != null && PluginWebValidation.IsLoopbackHttp(value.Trim())
            ? value.Trim().TrimEnd('/') : null;

    public void Save(InstalledPlugin package)
    {
        var items = Installed.Where(p => !p.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)).Append(package).ToArray();
        var temp = ManifestPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, ManifestPath, true);
        lock (_sync) _installedSnapshot = null;
        RaiseChanged();
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
        RaiseChanged();
    }
    /// <summary>Install plugins shipped next to the host; refresh a built-in folder when the shipped
    /// version changes, keep user installs and the existing enable flags.</summary>
    public void InstallBuiltins(string builtinRoot)
    {
        if (!Directory.Exists(builtinRoot)) return;
        foreach (var source in Directory.GetDirectories(builtinRoot))
        {
            var manifestPath = Path.Combine(source, "allagent-plugin.json");
            if (!File.Exists(manifestPath)) continue;
            var packageId = Path.GetFileName(source);
            var existing = Installed.FirstOrDefault(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !existing.Directory.EndsWith("-builtin", StringComparison.OrdinalIgnoreCase)) continue;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("内置插件清单无效：" + packageId);
            var assembly = Path.Combine(source, manifest.Assembly);
            var version = File.Exists(assembly) ? AssemblyName.GetAssemblyName(assembly).Version?.ToString(3) ?? "1.0.0" : "1.0.0";
            if (existing != null && existing.Version == version && Directory.Exists(Path.Combine(_root, existing.Directory))) continue;
            var folder = packageId.ToLowerInvariant() + "-" + version + "-builtin";
            var destination = Path.Combine(_root, folder);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            CopyDirectory(source, destination);
            if (existing != null)
            {
                var previous = Path.GetFullPath(Path.Combine(_root, existing.Directory));
                var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
                if (previous.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && !previous.Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(previous))
                    Directory.Delete(previous, true);
                Save(existing with { Version = version, Directory = folder });
            }
            else Save(new(packageId, version, folder));
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
            var context = new PluginContext(Path.Combine(directory, "data"), _services, package.PackageId, WebEvents, _settings); Directory.CreateDirectory(context.DataDirectory); plugin.Register(context);
            var web = ValidateWeb(directory, manifest.Web, context);
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
                if (web != null) _web[package.PackageId] = web;
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
        lock (_sync) { removed = _loaded.Where(l => l.Package.PackageId == package.PackageId).ToList(); _loaded.RemoveAll(l => l.Package.PackageId == package.PackageId); _web.Remove(package.PackageId); }
        foreach (var loaded in removed) { try { await loaded.Plugin.DisposeAsync(); } finally { loaded.Loader.Unload(); } }
        Save(package with { Enabled = enabled }); if (enabled) await LoadEnabledAsync();
    }
    public async Task RemoveAsync(string packageId)
    {
        await SetEnabledAsync(packageId, false);
        // Keep files recoverable; removing the manifest entry makes the package unavailable to the host.
        File.WriteAllText(ManifestPath + ".tmp", JsonSerializer.Serialize(Installed.Where(p => !p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))); File.Move(ManifestPath + ".tmp", ManifestPath, true);
        lock (_sync) _installedSnapshot = null;
        RaiseChanged();
    }
    public async ValueTask DisposeAsync()
    { foreach (var loaded in _loaded) { try { await loaded.Plugin.DisposeAsync(); } finally { loaded.Loader.Unload(); } } _loaded.Clear(); lock (_sync) _web.Clear(); }
}

public sealed record PluginManifest(string Assembly, string EntryType, int SdkVersion = 1, PluginWebManifest? Web = null);

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
