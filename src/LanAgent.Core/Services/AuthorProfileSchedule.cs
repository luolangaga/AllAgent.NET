using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Models;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Services;

public sealed record AuthorProfileUpdatePolicy(TimeSpan Interval, int BatchTurns, TimeProvider? Clock = null);

/// <summary>Bounded, durable pending author evidence. No model request is needed to decide eligibility.</summary>
internal sealed class AuthorProfileSchedule
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? PendingSince { get; set; }
    public int RelevantTurns { get; set; }
    public List<Message> Pending { get; set; } = [];
    public List<string> Seen { get; set; } = [];
    private static readonly Regex UserSignal = new(
        @"我(?:更|最|一直|平时|通常)?(?:喜欢|偏好|习惯|不喜欢|不用)|我的\s*(?:GitHub|账号|用户名|电脑|环境)|以后.{0,8}(?:默认|优先|都用)|(?:记住|记下|别再记|忘掉|忘记|删除|纠正|更新).{0,24}(?:偏好|档案|账号|GitHub|我|电脑|环境)|\b(?:I (?:prefer|like|dislike|usually use)|my (?:github|username|computer|environment)|remember (?:that|my)|forget (?:my|that))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EnvironmentSignal = new(
        @"(?:successfully installed|not (?:installed|found|recognized)|command not found|未安装|找不到.{0,20}(?:命令|程序)|无法识别|安装成功|版本|\bversion\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static (bool Immediate, List<Message> Evidence) Select(IReadOnlyList<Message> messages)
    {
        var immediate = false; var evidence = new List<Message>();
        foreach (var message in messages)
        {
            if (message.IsInternal || message.IsHostActivity) continue;
            if (message.Role == "user")
            {
                var text = ToolResult.Clip(message.InputText ?? message.Content, 4000);
                if (ContainsSecret(text)) continue;
                if (UserSignal.IsMatch(text)) { immediate = true; evidence.Add(new() { Role = "user", Content = text }); }
            }
            if (message.HostToolTraceJson == null) continue;
            try
            {
                foreach (var trace in JsonSerializer.Deserialize<HostToolTrace[]>(message.HostToolTraceJson, ToolResult.JsonOptions) ?? [])
                {
                    if (trace.ToolName != "run_shell") continue;
                    using var args = JsonDocument.Parse(trace.ArgsJson);
                    using var result = JsonDocument.Parse(trace.ResultJson);
                    var root = result.RootElement;
                    if (!args.RootElement.TryGetProperty("exe", out var exeNode)) continue;
                    var exe = Path.GetFileNameWithoutExtension(exeNode.GetString() ?? "");
                    if (!new[] { "dotnet", "node", "npm", "pnpm", "yarn", "python", "python3", "pip", "git", "gh", "pwsh", "rg" }.Contains(exe, StringComparer.OrdinalIgnoreCase)) continue;
                    var output = "";
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        if (data.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() != 0) continue;
                        foreach (var key in new[] { "stdout", "stderr" })
                            if (data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) output += value.GetString() + "\n";
                    }
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) output += error.GetString();
                    var versionCheck = args.RootElement.TryGetProperty("args", out var argv) && argv.ValueKind == JsonValueKind.Array &&
                        argv.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() is "--version" or "-v" or "-V");
                    output = ToolResult.Clip(output.Trim(), 2000);
                    if (output.Length == 0 || !(versionCheck || EnvironmentSignal.IsMatch(output))) continue;
                    // Do not persist outputs containing common credential labels or private-key blocks.
                    if (ContainsSecret(output)) continue;
                    evidence.Add(new() { Role = "tool", Content = "Verified environment observation from run_shell (" + exe + ", computer=" + Environment.MachineName + "):\n" + output });
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { /* Invalid trace is not evidence. */ }
        }
        return (immediate, evidence);
    }

    private static bool ContainsSecret(string text) => Regex.IsMatch(text,
        @"(?i)api[_ -]?key|password|access[_ -]?token|authorization|BEGIN .*PRIVATE KEY|gh[pousr]_[a-z0-9]{20,}|sk-[a-z0-9_-]{20,}|密码|密钥|口令|令牌",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public bool Admit(List<Message> evidence, bool immediate, DateTimeOffset now, AuthorProfileUpdatePolicy policy)
    {
        var added = false;
        foreach (var message in evidence)
        {
            var hash = Hash(message);
            var key = Key(message);
            var latest = Pending.LastOrDefault(m => Key(m) == key);
            if (!immediate && (latest != null ? Hash(latest) == hash : Seen.Contains(key + ":" + hash))) continue;
            Pending.Add(message); added = true;
        }
        if (added) { RelevantTurns++; PendingSince ??= now; }
        // Keep a strict budget for raw pending evidence, across all conversations and restarts.
        Pending = Pending.TakeLast(AgentMemory.MaxTranscriptMessages).ToList();
        while (Pending.Sum(m => m.Content.Length) > AgentMemory.MaxTranscriptCharacters) Pending.RemoveAt(0);
        if (Pending.Count == 0) return false;
        if (immediate) return true;
        if (LastAttempt is { } attempt && now - attempt < policy.Interval) return false;
        return RelevantTurns >= policy.BatchTurns || now - PendingSince!.Value >= policy.Interval;
    }

    public void Complete()
    {
        foreach (var message in Pending.Where(m => m.Role == "tool"))
        {
            var key = Key(message);
            Seen.RemoveAll(item => item.StartsWith(key + ":", StringComparison.Ordinal));
            Seen.Add(key + ":" + Hash(message));
        }
        Seen = Seen.TakeLast(64).ToList();
        Pending.Clear(); RelevantTurns = 0; PendingSince = null;
    }
    private static string Hash(Message message) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Role + "\n" + message.Content)));
    private static string Key(Message message) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Role + "\n" + message.Content.Split('\n', 2)[0])));

    public static AuthorProfileSchedule Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 100000) throw new IOException("作者档案更新队列过大；已跳过自动更新。");
        var state = JsonSerializer.Deserialize<AuthorProfileSchedule>(File.ReadAllText(path)) ?? throw new IOException("作者档案更新队列无效。");
        if (state.Pending == null || state.Seen == null || state.Pending.Count > AgentMemory.MaxTranscriptMessages || state.Pending.Any(m => m.Content == null) || state.Pending.Sum(m => (long)m.Content.Length) > AgentMemory.MaxTranscriptCharacters || state.Seen.Count > 64 || state.Pending.Count > 0 && state.PendingSince == null)
            throw new IOException("作者档案更新队列无效。");
        return state;
    }
    public void Save(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
