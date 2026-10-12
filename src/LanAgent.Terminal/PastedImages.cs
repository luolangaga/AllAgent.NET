using LanAgent.Core.Models;
using LanAgent.Core.Services;

namespace LanAgent.Terminal;

internal static class PastedImages
{
    public sealed record Result(string Text, string? Notice = null);
    public static async Task<Result> PrepareAsync(string text, string workspace, CancellationToken ct = default)
    {
        var input = text.Trim();
        if (input.Length == 0 || input.Length > 32000 || input.StartsWith('@')) return new(text);
        string Normalize(string path)
        {
            path = path.Trim().Trim('"', '\'');
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            return Path.GetFullPath(path, workspace);
        }
        bool Candidate(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
        try
        {
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return new(text);
            string[] paths;
            string? single = null;
            try { single = Normalize(input); } catch (ArgumentException) { }
            if (single != null && Candidate(single) && File.Exists(single)) paths = [single];
            else paths = CommandLineWords.Parse(input).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || paths.Any(path => !Candidate(path) || !File.Exists(path))) return new(text);
            if (paths.Length > 8) return new(text, "每次最多粘贴 8 张图片；未添加附件。");
            var images = new List<ImageAttachment>();
            foreach (var path in paths) images.Add(await ImageAttachment.ReadAsync(path, ct));
            ImageAttachment.Validate(images);
            return new(await StageAsync(images, ct), $"已添加 {images.Count} 张图片 · Alt+Backspace 移除最后一张 · 回车提交");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or FormatException)
        { return new(text, "图片附件未添加：" + ex.Message); }
    }
    internal static async Task<string> StageAsync(IReadOnlyList<ImageAttachment> images, CancellationToken ct = default)
    {
            ImageAttachment.Validate(images);
            var folder = Path.Combine(Path.GetTempPath(), "LanAgent-attachments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var references = new List<string>();
            for (var index = 0; index < images.Count; index++)
            {
                // Separate directories preserve names even when pasted files share a basename.
                var directory = Path.Combine(folder, index.ToString()); Directory.CreateDirectory(directory);
                var staged = Path.Combine(directory, Path.GetFileName(images[index].Name));
                await File.WriteAllBytesAsync(staged, Convert.FromBase64String(images[index].Base64), ct);
                references.Add("@\"" + staged + "\"");
            }
            return string.Join(' ', references) + " ";
    }
}
