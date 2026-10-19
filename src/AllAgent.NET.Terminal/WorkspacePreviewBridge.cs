using System.Text;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Terminal;

// Payloads of the browser's read-only workspace browser. Every path is resolved through the same
// workspace fence the file tools use, so the preview can never reach outside the workspace.
public sealed record WorkspaceTreeParams(string Path = ".", int Limit = 2000);
public sealed record WorkspaceTreeEntry(string Name, string Path, bool Directory, long Size, DateTime Modified, bool Link, bool Hidden);
public sealed record WorkspaceTreeResult(string Path, IReadOnlyList<WorkspaceTreeEntry> Entries, bool Truncated);
public sealed record WorkspaceReadParams(string Path);
public sealed record WorkspaceReadResult(string Path, string Kind, long Size, DateTime Modified, string? Text, string? Base64, bool Truncated);

internal sealed class WorkspacePreviewBridge(string workspace)
{
    private const int MaxEntries = 2000;
    private const int MaxTextBytes = 256 * 1024;
    private const int MaxImageBytes = 12 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg" };

    public void Register(BridgeDispatcher dispatcher)
    {
        dispatcher.Register<WorkspaceTreeParams, WorkspaceTreeResult>("workspace.tree", (parameters, _) => Task.FromResult(Tree(parameters)));
        dispatcher.Register<WorkspaceReadParams, WorkspaceReadResult>("workspace.readFile", (parameters, _) => Task.FromResult(Read(parameters)));
    }

    /// <summary>One directory level, lazily expanded by the page. Ignored names are returned too —
    /// hiding is the page's choice, not the host's.</summary>
    private WorkspaceTreeResult Tree(WorkspaceTreeParams parameters)
    {
        var policy = new WorkspacePolicy(workspace);
        var requested = (parameters.Path ?? ".").Replace('\\', '/').Trim();
        if (requested.Length == 0) requested = ".";
        var full = policy.Resolve(requested);
        if (!Directory.Exists(full)) throw new InvalidOperationException("目录不存在：" + requested);
        var root = Path.TrimEndingDirectorySeparator(policy.Root);
        var prefix = string.Equals(Path.TrimEndingDirectorySeparator(full), root, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Relative(root, full) + "/";
        var limit = Math.Clamp(parameters.Limit <= 0 ? MaxEntries : parameters.Limit, 1, MaxEntries);
        var entries = new List<WorkspaceTreeEntry>();
        var truncated = false;
        try
        {
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if (entries.Count >= limit) { truncated = true; break; }
                var link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                var directory = (info.Attributes & FileAttributes.Directory) != 0;
                long size = 0;
                if (!directory) { try { size = ((FileInfo)info).Length; } catch { size = 0; } }
                entries.Add(new WorkspaceTreeEntry(
                    info.Name,
                    prefix + info.Name,
                    directory,
                    size,
                    SafeModified(info),
                    link,
                    info.Name.StartsWith('.')));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        entries.Sort((left, right) =>
            left.Directory != right.Directory ? (left.Directory ? -1 : 1)
            : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return new WorkspaceTreeResult(requested == "." ? "." : Relative(root, full), entries, truncated);
    }

    private WorkspaceReadResult Read(WorkspaceReadParams parameters)
    {
        var policy = new WorkspacePolicy(workspace);
        var raw = (parameters.Path ?? string.Empty).Trim();
        if (raw.Length == 0) throw new ArgumentException("路径为空。");
        var full = policy.Resolve(raw.Replace('\\', '/'));
        if (Directory.Exists(full)) throw new InvalidOperationException("这是一个目录，无法预览文件内容。");
        if (!File.Exists(full)) throw new FileNotFoundException("文件不存在：" + raw);
        var info = new FileInfo(full);
        var path = Relative(Path.TrimEndingDirectorySeparator(policy.Root), full);
        if (ImageExtensions.Contains(info.Extension))
        {
            if (info.Length > MaxImageBytes) return new WorkspaceReadResult(path, "binary", info.Length, info.LastWriteTimeUtc, null, null, true);
            return new WorkspaceReadResult(path, "image", info.Length, info.LastWriteTimeUtc, null, Convert.ToBase64String(File.ReadAllBytes(full)), false);
        }
        var truncated = info.Length > MaxTextBytes;
        var length = (int)Math.Min(info.Length, MaxTextBytes);
        var bytes = new byte[length];
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(bytes, read, length - read);
                if (count <= 0) break;
                read += count;
            }
            length = read;
        }
        string? text = null;
        try
        {
            if (truncated)
            {
                // A truncation point can split a multi-byte character; keep the complete prefix only.
                var decoder = StrictUtf8.GetDecoder();
                var chars = new char[length + 1];
                decoder.Convert(bytes, 0, length, chars, 0, chars.Length, flush: false, out _, out var used, out _);
                text = new string(chars, 0, used);
            }
            else text = StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException) { }
        if (text == null || text.Contains('\0'))
            return new WorkspaceReadResult(path, "binary", info.Length, info.LastWriteTimeUtc, null, null, truncated);
        return new WorkspaceReadResult(path, "text", info.Length, info.LastWriteTimeUtc, text, null, truncated);
    }

    private static DateTime SafeModified(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');
}
