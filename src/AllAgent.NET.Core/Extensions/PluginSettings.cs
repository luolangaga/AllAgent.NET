using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllAgent.NET.Core.Extensions;

/// <summary>
/// Values declared by plugin web settings sections. Kept outside the conversation store:
/// settings belong to the shared plugin installation, not to one workspace or conversation.
/// </summary>
public sealed class PluginSettingsStore
{
    private const int MaxValueCharacters = 65_536;
    private const int MaxFileBytes = 1_048_576;
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, Dictionary<string, string>> _values;

    public PluginSettingsStore(string root)
    {
        _path = Path.Combine(Path.GetFullPath(root), "settings.json");
        _values = Load();
    }

    public string? Get(string packageId, string key)
    {
        ValidatePackage(packageId);
        ValidateKey(key);
        lock (_sync) return _values.TryGetValue(packageId, out var values) && values != null && values.TryGetValue(key, out var value) ? value : null;
    }

    public IReadOnlyDictionary<string, string> GetAll(string packageId)
    {
        ValidatePackage(packageId);
        lock (_sync) return _values.TryGetValue(packageId, out var values) && values != null
            ? new Dictionary<string, string>(values, StringComparer.Ordinal)
            : new Dictionary<string, string>();
    }

    public void Set(string packageId, string key, string? value)
    {
        ValidatePackage(packageId);
        ValidateKey(key);
        if (value is { Length: > MaxValueCharacters }) throw new ArgumentException("插件设置值超过 64KB 限制。");
        lock (_sync)
        {
            if (!_values.TryGetValue(packageId, out var values) || values == null)
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                _values[packageId] = values;
            }
            if (value == null) values.Remove(key);
            else values[key] = value;
            Save();
        }
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            if (new FileInfo(_path).Length > MaxFileBytes) throw new InvalidDataException("插件设置文件超过 1MB。");
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_path))
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > MaxFileBytes) throw new InvalidOperationException("插件设置文件超过 1MB 限制。");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidatePackage(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 200 || packageId.Any(char.IsControl))
            throw new ArgumentException("插件包 ID 无效。", nameof(packageId));
    }

    private static void ValidateKey(string key)
    {
        if (!KeyPattern.IsMatch(key ?? "")) throw new ArgumentException("插件设置 key 必须是小写字母、数字、下划线、点或短横线，最长 64 字符。", nameof(key));
    }
}

/// <summary>One plugin's namespace inside <see cref="PluginSettingsStore"/>.</summary>
public sealed class PluginSettings(string packageId, PluginSettingsStore store)
{
    public string? Get(string key) => store.Get(packageId, key);
    public void Set(string key, string? value) => store.Set(packageId, key, value);
    public IReadOnlyDictionary<string, string> GetAll() => store.GetAll(packageId);
}
