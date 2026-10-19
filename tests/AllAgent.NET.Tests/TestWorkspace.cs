using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private static readonly Lazy<RuntimeEnvironment> Installed = new(() => new EnvironmentDoctor().CheckAsync().GetAwaiter().GetResult());
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "AllAgent.NET.Tests", Guid.NewGuid().ToString("N"));
    public string Python { get; }
    public string Pwsh { get; }
    public AgentOptions Options { get; }
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        var env = Installed.Value;
        if (!env.Ready) throw new InvalidOperationException("Tests require Python and the bundled PowerShell SDK worker: " + string.Join(";", env.Problems));
        Python = env.Python!; Pwsh = env.PowerShell!;
        // Lifecycle fixtures script exact model requests; memory has its own opt-in integration coverage.
        Options = new AgentOptions { SharedDirectory = Path.Combine(Root, "author"), Permission = PermissionMode.Ask, WorkspaceRoot = Root, PythonPath = Python, PowerShellPath = Pwsh, CodeTimeout = TimeSpan.FromSeconds(30), AuditPath = Path.Combine(Root, "audit.jsonl") };
    }
    public SystemTools Tools => new(new WorkspacePolicy(Root), new(Python, Pwsh, []), new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe") });
    public void Dispose()
    {
        // Only this fixture-created directory is recursively removed.
        if (Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "AllAgent.NET.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(Root, true);
    }
}
