using System.IO;

namespace AllAgent.NET.Terminal;

/// <summary>
/// An all-users install lives in a directory a standard user cannot write (Program Files), so
/// anything the host would place next to the executable has to be able to move somewhere else.
/// </summary>
internal static class WritableDirectory
{
    /// <summary>True when a file can be created and removed in the directory.</summary>
    public static bool CanWrite(string directory)
    {
        var probe = System.IO.Path.Combine(directory, "allagent-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
