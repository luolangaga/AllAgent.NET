using LanAgent.Core.Runtime;
using LanAgent.Core.Tools;

namespace LanAgent.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private static readonly Lazy<RuntimeEnvironment> Installed = new(() => new EnvironmentDoctor().CheckAsync().GetAwaiter().GetResult());
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LanAgent.Tests", Guid.NewGuid().ToString("N"));
    public string Python { get; }
    public string Pwsh { get; }
    public AgentOptions Options { get; }
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        var env = Installed.Value;
        if (!env.Ready) throw new InvalidOperationException("Tests require Python and PowerShell 7.6: " + string.Join(";", env.Problems));
        Python = env.Python!; Pwsh = env.PowerShell!;
        // Lifecycle fixtures script exact model requests; memory has its own opt-in integration coverage.
        Options = new AgentOptions { EnableMemory = false, AuthorProfileDirectory = Path.Combine(Root, "author"), FullAccess = false, WorkspaceRoot = Root, PythonPath = Python, PowerShellPath = Pwsh, CodeTimeout = TimeSpan.FromSeconds(30), AuditPath = Path.Combine(Root, "audit.jsonl") };
    }
    public SystemTools Tools => new(new WorkspacePolicy(Root), new(Python, Pwsh, []), new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "LanAgent.ProcessProbe.exe") });
    public void Dispose()
    {
        // Only this fixture-created directory is recursively removed.
        if (Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "LanAgent.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(Root, true);
    }
}
