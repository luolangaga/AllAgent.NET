using System.Management.Automation;
using System.Management.Automation.Runspaces;

internal sealed class RunspaceBootstrap : IDisposable
{
    private Runspace _runspace;

    private RunspaceBootstrap(Runspace runspace, IReadOnlyList<string> moduleNames)
    {
        _runspace = runspace;
        ModuleNames = moduleNames;
    }

    internal Runspace Runspace => _runspace;
    internal IReadOnlyList<string> ModuleNames { get; }

    internal static RunspaceBootstrap Create(string sharedModules)
    {
        Environment.SetEnvironmentVariable("PSModulePath", sharedModules + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath"));
        var moduleNames = new List<string> { "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Security", "Microsoft.PowerShell.Diagnostics" };
        if (OperatingSystem.IsWindows()) moduleNames.Add("CimCmdlets");
        return new(CreateRunspace(moduleNames), moduleNames);
    }

    internal void Reset()
    {
        var previous = _runspace;
        _runspace = CreateRunspace(ModuleNames);
        previous.Dispose();
    }

    private static Runspace CreateRunspace(IReadOnlyList<string> moduleNames)
    {
        var state = InitialSessionState.CreateDefault2();
        // Resolve against SDK assets, rather than depending on an installed pwsh or its module path.
        var moduleRoot = Path.Combine(Path.GetDirectoryName(typeof(PSObject).Assembly.Location)!, "Modules");
        state.ImportPSModule(moduleNames.Select(name => Path.Combine(moduleRoot, name, name + ".psd1")).Where(File.Exists).ToArray());
        state.ThrowOnRunspaceOpenError = true;
        if (OperatingSystem.IsWindows()) state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;
        var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        runspace.SessionStateProxy.Path.SetLocation(Environment.CurrentDirectory);
        return runspace;
    }

    public void Dispose() => _runspace.Dispose();
}
