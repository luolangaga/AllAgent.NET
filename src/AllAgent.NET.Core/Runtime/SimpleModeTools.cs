namespace AllAgent.NET.Core.Runtime;

/// <summary>Simple mode keeps one execution surface: PowerShell, reading a file and editing a file.
/// Everything else stays registered on the host but is neither listed to the model nor callable from
/// execute_code while the mode is active, and instruction skills are dropped for the turn.</summary>
public static class SimpleModeTools
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "powershell_script", "file_read", "file_edit"
    };
}
