using System.Text.Json;

namespace AllAgent.NET.Terminal;

internal sealed record BrowserProfile(string Name, string UserDataDirectory, string DirectoryName)
{
    public string Path => System.IO.Path.Combine(UserDataDirectory, DirectoryName);
    public string Label => Name + " · " + Path;
}

internal static class BrowserProfiles
{
    internal static string DefaultRoot(string kind) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        kind == "edge" ? @"Microsoft\Edge\User Data" : @"Google\Chrome\User Data");

    internal static IReadOnlyList<BrowserProfile> Read(string root)
    {
        if (!Directory.Exists(root)) return [];
        var names = new Dictionary<string, string>();
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root, "Local State")));
            if (state.RootElement.TryGetProperty("profile", out var profile) && profile.TryGetProperty("info_cache", out var cache))
                foreach (var item in cache.EnumerateObject())
                    if (item.Value.TryGetProperty("name", out var name)) names[item.Name] = name.GetString() ?? item.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return Directory.EnumerateDirectories(root).Where(path =>
                File.Exists(System.IO.Path.Combine(path, "Preferences")) || names.ContainsKey(System.IO.Path.GetFileName(path)))
            .Select(path => new BrowserProfile(names.GetValueOrDefault(System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(path)), root, System.IO.Path.GetFileName(path)))
            .OrderBy(p => p.DirectoryName == "Default" ? 0 : 1).ThenBy(p => p.Name).ToArray();
    }

    internal static BrowserProfile FromPath(string path)
    {
        path = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        if (File.Exists(System.IO.Path.Combine(path, "Local State")))
            throw new ArgumentException("请选择具体配置目录，例如 User Data\\Default 或 User Data\\Profile 1，而不是 User Data 根目录。");
        if (!File.Exists(System.IO.Path.Combine(path, "Preferences")))
            throw new ArgumentException("该目录不是已有浏览器配置：找不到 Preferences。请打开 edge://version 或 chrome://version 查看“个人资料路径”。");
        var parent = Directory.GetParent(path)!.FullName;
        return Read(parent).First(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? DebugEndpoint(string root)
    {
        try
        {
            var lines = File.ReadAllLines(System.IO.Path.Combine(root, "DevToolsActivePort"));
            if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535 &&
                lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) && !lines[1].Any(char.IsWhiteSpace) && !lines[1].Contains('?') && !lines[1].Contains('#'))
                return "ws://127.0.0.1:" + port + lines[1];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
