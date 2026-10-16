using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
try
{
    var sharedModules = Environment.GetEnvironmentVariable("LANAGENT_PS_MODULE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent", "powershell", "Modules");
    Environment.SetEnvironmentVariable("PSModulePath", sharedModules + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath"));
    var state = InitialSessionState.CreateDefault2();
    var moduleNames = new List<string> { "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Security" };
    if (OperatingSystem.IsWindows()) moduleNames.Add("CimCmdlets");
    // Resolve against SDK assets, rather than depending on an installed pwsh or its module path.
    var moduleRoot = Path.Combine(Path.GetDirectoryName(typeof(PSObject).Assembly.Location)!, "Modules");
    state.ImportPSModule(moduleNames.Select(name => Path.Combine(moduleRoot, name, name + ".psd1")).ToArray());
    state.ThrowOnRunspaceOpenError = true;
    if (OperatingSystem.IsWindows()) state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;
    using var runspace = RunspaceFactory.CreateRunspace(state);
    runspace.Open();
    runspace.SessionStateProxy.Path.SetLocation(Environment.CurrentDirectory);
    using var shell = PowerShell.Create();
    shell.Runspace = runspace;
    if (args is ["--invoke" or "--command-info", var invokePayload])
        return CommandInvocation.Run(shell, JsonSerializer.Deserialize<InvocationRequest>(Convert.FromBase64String(invokePayload))!, args[0] == "--command-info");
    if (args is ["--modules", var modulePayload])
    {
        var moduleRequest = JsonSerializer.Deserialize<ModuleRequest>(Convert.FromBase64String(modulePayload)) ?? throw new ArgumentException("Missing module request.");
        shell.AddScript("""
            param($operation, $name, $version, $root)
            $ErrorActionPreference = 'Stop'
            if ($operation -in @('search', 'install')) {
                Import-Module (Join-Path $root 'Microsoft.PowerShell.PSResourceGet/1.2.0/Microsoft.PowerShell.PSResourceGet.psd1') -ErrorAction Stop
                if ($operation -eq 'search') {
                    $items = @(Find-PSResource -Name $name -Repository PSGallery -Type Module | Select-Object -First 30 Name, @{Name='Version'; Expression={$_.Version.ToString()}}, Description, Author)
                    ConvertTo-Json -InputObject $items -Depth 5 -Compress
                    return
                }
                $parameters = @{ Name=$name; Repository='PSGallery'; Path=$root; TrustRepository=$true; AcceptLicense=$true }
                if ($version) { $parameters.Version = $version }
                Save-PSResource @parameters
            } elseif ($operation -ne 'list') { throw 'Unknown module operation' }
            $items = @(@{ name='Microsoft.PowerShell.Core'; version=$PSVersionTable.PSVersion.ToString(); commands=@(Get-Command -Module Microsoft.PowerShell.Core | Select-Object -ExpandProperty Name | Sort-Object -Unique); error=$null })
            $candidates = @(Get-Module) + @(Get-Module -ListAvailable | Where-Object { $_.ModuleBase.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
            foreach ($module in ($candidates | Sort-Object Name, Version -Unique)) {
                $failure = $null
                try {
                    $loaded = Import-Module $module.Path -PassThru -ErrorAction Stop
                    $commands = @($loaded.ExportedCommands.Keys | Sort-Object)
                } catch { $failure = $_.Exception.Message; $commands = @() }
                $items += @{ name=$module.Name; version=$module.Version.ToString(); commands=$commands; error=$failure }
            }
            ConvertTo-Json -InputObject $items -Depth 6 -Compress
            """).AddArgument(moduleRequest.operation).AddArgument(moduleRequest.name).AddArgument(moduleRequest.version).AddArgument(sharedModules);
        var result = shell.Invoke();
        if (shell.HadErrors) throw new InvalidOperationException(string.Join("; ", shell.Streams.Error.Select(e => e.ToString())));
        foreach (var item in result) Console.WriteLine(item.ToString());
        return 0;
    }
    if (args is ["--probe"])
    {
        shell.AddScript("$PSVersionTable.PSVersion.ToString()");
        var version = shell.Invoke();
        if (shell.HadErrors || version.Count != 1) return 1;
        Console.WriteLine(JsonSerializer.Serialize(new { runtime = "LanAgent.PowerShell.SDK", version = version[0].ToString(), modules = moduleNames }));
        return 0;
    }
    if (args is not ["--execute", var payload]) throw new ArgumentException("Expected --probe or --execute <base64-json>.");
    var request = JsonSerializer.Deserialize<Request>(Convert.FromBase64String(payload)) ?? throw new ArgumentException("Missing request.");
    if (!Path.IsPathFullyQualified(request.exe) || !File.Exists(request.exe)) throw new ArgumentException("Executable must exist at an absolute path.");
    if (request.args == null || request.args.Any(value => value == null || value.Contains('\0'))) throw new ArgumentException("Invalid literal arguments.");
    // Only this constant source is parsed. The program and its arguments enter as typed data.
    shell.AddScript("""
        param($executable, [string[]]$literalArguments)
        $ErrorActionPreference = 'Stop'
        $PSStyle.OutputRendering = 'PlainText'
        $PSNativeCommandArgumentPassing = 'Standard'
        $env:NO_COLOR = '1'
        $env:TERM = 'dumb'
        & $executable @literalArguments
        """).AddArgument(request.exe).AddArgument(request.args);
    using var output = new PSDataCollection<PSObject>();
    output.DataAdded += (_, _) => { foreach (var item in output.ReadAll()) Console.Out.WriteLine(item?.ToString()); };
    shell.Streams.Error.DataAdded += (_, _) => { foreach (var item in shell.Streams.Error.ReadAll()) Console.Error.WriteLine(item.ToString()); };
    shell.Streams.Warning.DataAdded += (_, _) => { foreach (var item in shell.Streams.Warning.ReadAll()) Console.Error.WriteLine(item.Message); };
    shell.Streams.Information.DataAdded += (_, _) => { foreach (var item in shell.Streams.Information.ReadAll()) Console.Out.WriteLine(item.MessageData); };
    using var input = new PSDataCollection<PSObject>();
    input.Complete();
    shell.EndInvoke(shell.BeginInvoke<PSObject, PSObject>(input, output));
    return runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int exitCode ? exitCode : shell.HadErrors ? 1 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal sealed record Request(string exe, string[] args);
internal sealed record ModuleRequest(string operation, string? name, string? version);
