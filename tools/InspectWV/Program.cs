using System.Reflection;
using System.IO;

var avDir = @"C:\Users\luolan\.nuget\packages\avalonia\11.2.3\lib\net8.0\";
if (Directory.Exists(avDir))
    foreach (var dll in Directory.GetFiles(avDir, "*.dll"))
        try { Assembly.LoadFrom(dll); } catch {}

var path = @"C:\Users\luolan\.nuget\packages\avalonia.controls.webview\11.4.0\lib\net8.0\Avalonia.Controls.WebView.dll";
try
{
    var asm = Assembly.LoadFrom(path);
    foreach (var t in asm.GetTypes())
    {
        if (t.FullName == null || t.Name.StartsWith("<")) continue;
        if (t.FullName.Contains("CodeAnalysis") || t.FullName.Contains("CompilerServices") || t.FullName.Contains("Diagnostics")) continue;
        Console.WriteLine($"\nTYPE: {t.FullName}");
        Console.WriteLine($"  Base: {t.BaseType?.FullName}");
        Console.WriteLine($"  Interfaces: {string.Join(", ", t.GetInterfaces().Select(i => i.FullName))}");
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  {m}");
    }
}
catch (ReflectionTypeLoadException ex)
{
    Console.Error.WriteLine("Loader exceptions:");
    foreach (var le in ex.LoaderExceptions.Where(e => e != null).Take(3))
        Console.Error.WriteLine("  " + le!.Message);
    foreach (var t in ex.Types.Where(t => t != null && !t.Name.StartsWith("<")))
    {
        if (t!.FullName == null) continue;
        if (t.FullName.Contains("CodeAnalysis") || t.FullName.Contains("CompilerServices") || t.FullName.Contains("Diagnostics")) continue;
        Console.WriteLine($"\nTYPE: {t.FullName}");
        try {
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine($"  {m}");
        } catch {}
    }
}
