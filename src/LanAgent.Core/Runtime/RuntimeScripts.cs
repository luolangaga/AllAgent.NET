namespace LanAgent.Core.Runtime;

internal static class RuntimeScripts
{
    public static string Read(string name)
    {
        using var stream = typeof(RuntimeScripts).Assembly.GetManifestResourceStream("LanAgent.Core.Runtime.Scripts." + name)
            ?? throw new InvalidOperationException($"Missing trusted runtime script: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
