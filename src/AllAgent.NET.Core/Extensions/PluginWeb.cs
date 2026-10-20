using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Bridge;

namespace AllAgent.NET.Core.Extensions;

/// <summary>Optional "web" block of allagent-plugin.json: the plugin's own UI payload plus the
/// contributions it makes to the host frontend. Entries are paths relative to the install
/// directory (inside web/); packages ship them under allagent/web/.</summary>
public sealed record PluginWebManifest(
    int SdkVersion = 1,
    string? Entry = null,
    IReadOnlyList<string>? Capabilities = null,
    string? DevUrl = null,
    PluginWebContributions? Contributes = null);

/// <summary>Declarative contributions rendered by the host frontend. Lists are normalized (never
/// null) once a plugin is loaded.</summary>
public sealed record PluginWebContributions(
    IReadOnlyList<PluginWebPage>? Pages = null,
    IReadOnlyList<PluginWebPanel>? Panels = null,
    IReadOnlyList<PluginWebToolCard>? ToolCards = null,
    IReadOnlyList<PluginWebCommand>? Commands = null,
    IReadOnlyList<PluginWebSettingsSection>? Settings = null,
    IReadOnlyList<PluginWebComposerAction>? ComposerActions = null,
    IReadOnlyList<PluginWebStatusItem>? StatusBar = null)
{
    public static readonly PluginWebContributions Empty = new();
}

public sealed record PluginWebPage(string Id, string Title, string Entry, string? Icon = null, int Order = 50);
public sealed record PluginWebPanel(string Id, string Title, string Entry, int Order = 50);
public sealed record PluginWebToolCard(string Tool, string Entry);
public sealed record PluginWebCommand(string Id, string Title, string? Page = null);
public sealed record PluginWebSettingsSection(string Id, string Title, IReadOnlyList<PluginWebSettingField>? Fields = null, string? Entry = null);
public sealed record PluginWebSettingField(string Key, string Label, string Type = "text", IReadOnlyList<string>? Options = null, string? Default = null, string? Description = null, string? Placeholder = null);
public sealed record PluginWebComposerAction(string Id, string Title, string? Page = null, string? Icon = null);
public sealed record PluginWebStatusItem(string Id, string Label);

/// <summary>The snapshot the browser reads through the `web.contributions` bridge method.</summary>
public sealed record PluginWebSnapshot(int SdkVersion, string? AssetBaseUrl, IReadOnlyList<PluginWebInfo> Plugins);
public sealed record PluginWebInfo(
    string PackageId, string PluginId, string Name, string Version,
    bool Enabled, bool WebEnabled, bool Active,
    IReadOnlyList<string> Capabilities, PluginWebContributions Contributes,
    string? DevUrl, string? Error);

/// <summary>Web summary attached to PluginStatus for the terminal panel and management UIs.</summary>
public sealed record PluginWebStatus(bool WebEnabled, int Pages, int Panels, int ToolCards, IReadOnlyList<string> Capabilities, string? Error)
{
    public int Settings { get; init; }
}

/// <summary>One backend method a plugin exposes to its own browser frames. The host registers it
/// as plugin.&lt;packageId&gt;.&lt;name&gt;; other plugins and host methods stay unreachable.</summary>
public sealed record PluginWebBridgeHandler(string Name, Func<JsonElement?, CancellationToken, Task<JsonElement?>> Handler);

/// <summary>Fan-out for plugin backend → browser frame events. The web UI host subscribes and
/// rebroadcasts them as plugin.&lt;packageId&gt;.&lt;event&gt; bridge events.</summary>
public sealed class PluginWebEvents
{
    public event Action<string, string, object?>? Pushed;
    internal void Push(string packageId, string eventName, object? data) => Pushed?.Invoke(packageId, eventName, data);
}

/// <summary>Plugin-side web channel: pushes events to the frames of this plugin. A push with no
/// connected web UI is a no-op.</summary>
public sealed class PluginWebChannel(string packageId, PluginWebEvents events)
{
    public void Push(string eventName, object? data)
    {
        if (!PluginWebValidation.IsValidName(eventName)) throw new ArgumentException("Web 事件名无效：只允许字母、数字、下划线、点和短横线，最长 64 字符。");
        events.Push(packageId, eventName, data);
    }
}

internal static class PluginWebValidation
{
    private static readonly Regex SettingKey = new("^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');

    public static bool IsValidSettingKey(string? key) => key != null && SettingKey.IsMatch(key);

    public static bool IsLoopbackHttp(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]");

    /// <summary>Normalizes a declared entry to an install-directory-relative path and verifies it
    /// resolves to an existing file inside web/.</summary>
    public static string ResolveEntry(string? entry, string installDirectory, string webRoot, string what)
    {
        var relative = (entry ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (relative.Length == 0) throw new InvalidDataException(what + "缺少入口文件。");
        var full = Path.GetFullPath(Path.Combine(installDirectory, relative));
        if (!full.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(what + "入口必须位于 web/ 目录内：" + relative);
        if (!File.Exists(full)) throw new InvalidDataException(what + "入口文件不存在：" + relative);
        return relative;
    }

    public static PluginWebContributions Normalize(PluginWebContributions? source, string installDirectory, string webRoot)
    {
        source ??= new();
        var pages = (source.Pages ?? []).Select(page => new PluginWebPage(page.Id, string.IsNullOrWhiteSpace(page.Title) ? page.Id : page.Title, ResolveEntry(page.Entry, installDirectory, webRoot, "页面"), page.Icon, page.Order)).ToArray();
        var panels = (source.Panels ?? []).Select(panel => new PluginWebPanel(panel.Id, string.IsNullOrWhiteSpace(panel.Title) ? panel.Id : panel.Title, ResolveEntry(panel.Entry, installDirectory, webRoot, "面板"), panel.Order)).ToArray();
        var toolCards = (source.ToolCards ?? []).Select(card => new PluginWebToolCard(card.Tool, ResolveEntry(card.Entry, installDirectory, webRoot, "工具卡"))).ToArray();
        var commands = (source.Commands ?? []).Select(command => new PluginWebCommand(command.Id, string.IsNullOrWhiteSpace(command.Title) ? command.Id : command.Title, command.Page)).ToArray();
        var settings = (source.Settings ?? []).Select(section =>
        {
            var fields = (section.Fields ?? []).Select(field =>
            {
                if (!IsValidSettingKey(field.Key)) throw new InvalidDataException("设置项 key 无效（仅小写字母、数字、_ - .）：" + field.Key);
                if (string.IsNullOrWhiteSpace(field.Label)) throw new InvalidDataException("设置项缺少标签：" + field.Key);
                var type = string.IsNullOrWhiteSpace(field.Type) ? "text" : field.Type.Trim().ToLowerInvariant();
                if (type is not ("text" or "number" or "boolean" or "select" or "password")) throw new InvalidDataException("设置项类型无效：" + field.Key + " / " + type);
                if (type == "select" && (field.Options == null || field.Options.Count == 0)) throw new InvalidDataException("select 设置项缺少 options：" + field.Key);
                return new PluginWebSettingField(field.Key, field.Label, type, field.Options, field.Default, field.Description, field.Placeholder);
            }).ToArray();
            if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw new InvalidDataException("设置区字段 key 冲突：" + section.Id);
            var entry = section.Entry == null ? null : ResolveEntry(section.Entry, installDirectory, webRoot, "设置区");
            if (fields.Length == 0 && entry == null) throw new InvalidDataException("设置区至少需要 fields 或 entry：" + section.Id);
            return new PluginWebSettingsSection(section.Id, string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title, fields, entry);
        }).ToArray();
        var actions = (source.ComposerActions ?? []).Select(action => new PluginWebComposerAction(action.Id, string.IsNullOrWhiteSpace(action.Title) ? action.Id : action.Title, action.Page, action.Icon)).ToArray();
        var statusBar = (source.StatusBar ?? []).Select(item => new PluginWebStatusItem(item.Id, string.IsNullOrWhiteSpace(item.Label) ? item.Id : item.Label)).ToArray();
        ValidateIds(pages.Select(page => page.Id), "页面");
        ValidateIds(panels.Select(panel => panel.Id), "面板");
        ValidateIds(commands.Select(command => command.Id), "命令");
        ValidateIds(settings.Select(section => section.Id), "设置区");
        ValidateIds(actions.Select(action => action.Id), "组合器动作");
        ValidateIds(statusBar.Select(item => item.Id), "状态栏项");
        if (toolCards.Any(card => string.IsNullOrWhiteSpace(card.Tool))) throw new InvalidDataException("工具卡缺少工具名（tool）。");
        return new(pages, panels, toolCards, commands, settings, actions, statusBar);
    }

    public static void ValidateIds(IEnumerable<string?> ids, string what)
    {
        var values = ids.Select(id => id ?? string.Empty).ToArray();
        if (values.Any(value => !IsValidName(value)) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new InvalidDataException(what + " ID 为空、无效或冲突；只允许字母、数字、下划线、点和短横线。");
    }
}
