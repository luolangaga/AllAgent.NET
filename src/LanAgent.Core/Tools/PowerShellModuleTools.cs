using System.ComponentModel;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

public partial class PowerShellModuleTools(PowerShellModuleStore store)
{
    [AgentTool("powershell_capabilities"), Description("Read verified installed PowerShell modules and exported command names. Invoke commands with powershell_invoke or .ps1 files through run_shell. This tool never installs modules; non-null error means module import failed.")]
    public Task<ToolResult> CapabilitiesAsync([Description("Exact module name; empty for all modules")] string module = "", [Description("Command offset, at least zero")] int skip = 0, [Description("Commands per module, 1 to 100")] int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ToolResult.Success(store.Commands(module, skip, limit)));
    }
}
