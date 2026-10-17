using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Terminal;

internal sealed class TerminalArguments
{
    public string Command { get; private set; } = "chat";
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Shell { get; } = new(StringComparer.OrdinalIgnoreCase);
    private string? _dataDirectory;
    private string? _sharedDirectory;

    public static TerminalArguments Parse(string[] args)
    {
        var result = new TerminalArguments();
        var valued = new HashSet<string> { "workspace", "config", "resume", "data-dir", "python", "pwsh", "worker", "guest", "max-tools", "timeout", "code-timeout", "allow-shell", "output", "provider", "protocol", "base-url", "model", "models", "key-env", "reasoning-protocol", "effort", "budget", "max-tokens", "name", "scope", "description", "instructions-file", "command", "args-json", "transport", "env-json", "headers-json" };
        valued.Add("nuget-source");
        valued.Add("context-window");
        valued.Add("subagent-model");
        valued.Add("subagent-provider");
        valued.Add("background-concurrency");
        var flagged = new HashSet<string> { "help", "json", "verbose", "install", "inline", "trust-plugin", "full-access", "ask", "no-initialize" };
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith("--")) { result.Command = args[0]; index++; }
        for (; index < args.Length; index++)
        {
            var token = args[index];
            if (token == "--") { result.Positionals.AddRange(args[(index + 1)..]); break; }
            if (!token.StartsWith("--")) { result.Positionals.Add(token); continue; }
            var name = token[2..];
            if (flagged.Contains(name)) { result.Flags.Add(name); continue; }
            if (!valued.Contains(name)) throw new ArgumentException("Unknown option: " + token);
            if (++index >= args.Length) throw new ArgumentException("Missing value: " + token);
            if (name == "allow-shell")
            {
                var pair = args[index].Split('=', 2);
                if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || !Path.IsPathFullyQualified(pair[1]) || !File.Exists(pair[1]))
                    throw new ArgumentException("--allow-shell requires alias=absolute-executable-path");
                result.Shell.Add(pair[0], Path.GetFullPath(pair[1]));
            }
            else if (!result.Values.TryAdd(name, args[index])) throw new ArgumentException("Duplicate option: " + token);
        }
        if (result.Flags.Contains("json") && result.Command == "chat") throw new ArgumentException("Use run --json for noninteractive JSON events.");
        _ = result.BackgroundConcurrency;
        return result;
    }

    public string? Get(string name) => Values.GetValueOrDefault(name);
    public string Workspace => Path.GetFullPath(Get("workspace") ?? Environment.CurrentDirectory);
    public string DataDirectory => _dataDirectory ??= Path.GetFullPath(Get("data-dir") ?? AllAgent.NET.Core.Data.LegacyDataPaths.ProjectDirectory(Workspace));
    public string SharedDirectory => _sharedDirectory ??= Path.GetFullPath(Get("data-dir") ?? AllAgent.NET.Core.Data.LegacyDataPaths.SharedDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
    public int BackgroundConcurrency => Get("background-concurrency") is { } value
        ? int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : throw new ArgumentException("--background-concurrency requires a nonnegative integer; 0 means unlimited.")
        : 0;
    public int Number(string name, int fallback) => Get(name) is { } value ? int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException("Invalid positive integer: " + name) : fallback;
    public AgentOptions Options(ToolsApproval approval)
    {
        DiscoverShellExecutables(Shell);
        var workspace = Path.GetFullPath(Get("workspace") ?? Environment.CurrentDirectory);
        return new AgentOptions
        {
            WorkspaceRoot = workspace, SharedDirectory = SharedDirectory, PythonPath = Get("python"), PowerShellPath = Get("pwsh"),
            SandboxWorkerPath = Get("worker") ?? Path.Combine(AppContext.BaseDirectory, "sandbox", "AllAgent.NET.Sandbox.exe"),
            PythonGuestPath = Get("guest") ?? Environment.GetEnvironmentVariable("HYPERLIGHT_PYTHON_GUEST_PATH"),
            FullAccess = !Flags.Contains("ask"), MaxToolCalls = Number("max-tools", int.MaxValue),
            RunTimeout = Get("timeout") == null ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Number("timeout", 600)), CodeTimeout = TimeSpan.FromSeconds(Number("code-timeout", !Flags.Contains("ask") ? 600 : 60)),
            ShellAllowlist = Shell, Approver = approval.ApproveAsync,
            AuditPath = Path.Combine(DataDirectory, "audit.jsonl")
        };
    }

    internal static void DiscoverShellExecutables(IDictionary<string, string> shell)
    {
        foreach (var alias in new[] { "git", "rg", "dotnet", "node" })
            if (!shell.ContainsKey(alias) && EnvironmentDoctor.FindExecutable(alias + ".exe") is { } path) shell.Add(alias, path);
        // PowerShell scripts forward literal arrays to node; avoid cmd's second command-line parser.
        foreach (var alias in new[] { "npm", "npx", "pnpm", "yarn" })
            if (!shell.ContainsKey(alias) && (EnvironmentDoctor.FindExecutable(alias + ".ps1") ?? EnvironmentDoctor.FindExecutable(alias + ".exe")) is { } path) shell.Add(alias, path);
    }
}
