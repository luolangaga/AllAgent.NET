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
    private string? _workspace;

    public static TerminalArguments Parse(string[] args)
    {
        var result = new TerminalArguments();
        var valued = new HashSet<string> { "workspace", "config", "resume", "data-dir", "python", "pwsh", "worker", "guest", "max-tools", "timeout", "code-timeout", "allow-shell", "output", "provider", "protocol", "base-url", "model", "models", "key-env", "reasoning-protocol", "effort", "budget", "max-tokens", "name", "scope", "description", "instructions-file", "command", "args-json", "transport", "env-json", "headers-json" };
        valued.Add("nuget-source");
        valued.Add("context-window");
        valued.Add("subagent-model");
        valued.Add("subagent-provider");
        valued.Add("background-concurrency");
        valued.Add("gui-url");
        valued.Add("host");
        valued.Add("port");
        valued.Add("password");
        valued.Add("source");
        valued.Add("update-health");
        valued.Add("update-result");
        var flagged = new HashSet<string> { "help", "json", "verbose", "install", "inline", "trust-plugin", "full-access", "restricted", "ask", "no-initialize", "guimode", "gui", "gui-devtools", "lan", "no-lan", "clear-password", "check", "yes", "force", "pre", "rollback", "allow-unsigned", "require-signature", "allow-missing-checksum", "no-restart" };
        var index = 0;
        // Options may be spelled with a single dash when the name is registered (-guimode);
        // every other bare token stays the command word or a positional.
        if (args.Length > 0 && OptionName(args[0], flagged, valued) == null) { result.Command = args[0]; index++; }
        for (; index < args.Length; index++)
        {
            var token = args[index];
            if (token == "--") { result.Positionals.AddRange(args[(index + 1)..]); break; }
            var name = OptionName(token, flagged, valued);
            if (name == null) { result.Positionals.Add(token); continue; }
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
        // -guimode / --gui is the silent entry point: a window host is detached and the
        // terminal returns to the prompt; `allagent gui` keeps the host in the foreground.
        if (result.GuiMode && result.Command is not ("chat" or "gui")) throw new ArgumentException("-guimode 不能与其他命令同时使用。");
        if (result.GuiMode) result.Command = "gui";
        _ = result.BackgroundConcurrency;
        return result;
    }

    /// <summary>Resolves an option token, accepting the documented single-dash spelling (-guimode).</summary>
    private static string? OptionName(string token, HashSet<string> flagged, HashSet<string> valued)
    {
        if (token.Length < 2 || token[0] != '-') return null;
        var name = token[1] == '-' ? token[2..] : token[1..];
        if (name.Length == 0) return null;
        if (flagged.Contains(name) || valued.Contains(name)) return name;
        // Unknown long options stay hard errors so typos never run as positional arguments;
        // single-dash tokens that match nothing keep their historic meaning as positionals.
        return token[1] == '-' ? name : null;
    }

    public string? Get(string name) => Values.GetValueOrDefault(name);
    /// <summary>Requested through -guimode / --guimode / --gui.</summary>
    public bool GuiMode => Flags.Contains("guimode") || Flags.Contains("gui");
    public string Workspace => _workspace ??= ResolveWorkspace();
    /// <summary>True when the current directory is read-only and the user profile stands in for it.</summary>
    public bool WorkspaceFallback { get; private set; }
    public string DataDirectory => _dataDirectory ??= Path.GetFullPath(Get("data-dir") ?? AllAgent.NET.Core.Data.LegacyDataPaths.ProjectDirectory(Workspace));
    public string SharedDirectory => _sharedDirectory ??= Path.GetFullPath(Get("data-dir") ?? AllAgent.NET.Core.Data.LegacyDataPaths.SharedDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
    /// <summary>Manifest that owns app settings: an explicit --data-dir keeps the legacy single file.</summary>
    public string SettingsDirectory => Get("data-dir") == null ? SharedDirectory : DataDirectory;
    public int BackgroundConcurrency => Get("background-concurrency") is { } value
        ? int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : throw new ArgumentException("--background-concurrency requires a nonnegative integer; 0 means unlimited.")
        : 0;
    public int Number(string name, int fallback) => Get(name) is { } value ? int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException("Invalid positive integer: " + name) : fallback;
    /// <summary>Host permission mode: restricted by default; --ask or --full-access override it (--ask wins).</summary>
    public PermissionMode Permission => Flags.Contains("ask") ? PermissionMode.Ask
        : Flags.Contains("full-access") ? PermissionMode.Full
        : PermissionMode.Restricted;
    public AgentOptions Options(ToolsApproval approval)
    {
        DiscoverShellExecutables(Shell);
        var workspace = Workspace;
        return new AgentOptions
        {
            WorkspaceRoot = workspace, SharedDirectory = SharedDirectory, PythonPath = Get("python"), PowerShellPath = Get("pwsh"),
            SandboxWorkerPath = Get("worker") ?? Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.Sandbox.exe"),
            PythonGuestPath = Get("guest") ?? Environment.GetEnvironmentVariable("HYPERLIGHT_PYTHON_GUEST_PATH"),
            Permission = Permission, MaxToolCalls = Number("max-tools", int.MaxValue),
            RunTimeout = Get("timeout") == null ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Number("timeout", 600)), CodeTimeout = TimeSpan.FromSeconds(Number("code-timeout", Permission == PermissionMode.Ask ? 60 : 600)),
            ShellAllowlist = Shell, Approver = approval.ApproveAsync,
            AuditPath = Path.Combine(DataDirectory, "audit.jsonl")
        };
    }

    /// <summary>
    /// The workspace defaults to the current directory, but an all-users install lives in Program
    /// Files: running the executable from its own directory would otherwise make a read-only
    /// folder the workspace. The user profile stands in, and the caller reports the substitution.
    /// </summary>
    private string ResolveWorkspace()
    {
        if (Get("workspace") is { } requested) return Path.GetFullPath(requested);
        if (WritableDirectory.CanWrite(Environment.CurrentDirectory)) return Path.GetFullPath(Environment.CurrentDirectory);
        WorkspaceFallback = true;
        return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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
