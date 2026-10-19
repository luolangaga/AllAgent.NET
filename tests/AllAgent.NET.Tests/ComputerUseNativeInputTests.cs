using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ComputerUseNativeInputTests
{
    [Fact]
    public void NativeInputHasWindowsAbiLayoutAndUnicodePairsWithoutModifierChanges()
    {
        var (context, type) = Load();
        try
        {
            var input = Assert.IsAssignableFrom<Array>(type.GetMethod("BuildText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["A中😀\r\n\t"]));
            var element = input.GetType().GetElementType()!;
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(element));
            Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf(element, "Data").ToInt32());
            Assert.Equal(12, input.Length); // Four UTF-16 units plus RETURN and TAB, each down/up.
            for (var i = 0; i < 8; i += 2)
            {
                Assert.Equal((ushort)0, Key(input.GetValue(i)!, "Key"));
                Assert.Equal(4u, Key(input.GetValue(i)!, "Flags")); Assert.Equal(6u, Key(input.GetValue(i + 1)!, "Flags"));
                Assert.Equal(Key(input.GetValue(i)!, "Scan"), Key(input.GetValue(i + 1)!, "Scan"));
            }
            Assert.Equal((ushort)13, Key(input.GetValue(8)!, "Key")); Assert.Equal((ushort)9, Key(input.GetValue(10)!, "Key"));
        }
        finally { context.Unload(); }
    }
    [Fact]
    public void ShortcutReleasesModifiersInReverseAndMarksExtendedNavigationKeys()
    {
        var (context, type) = Load();
        try
        {
            var method = type.GetMethod("BuildChord", BindingFlags.Static | BindingFlags.NonPublic)!;
            var keyType = method.GetParameters()[0].ParameterType.GetElementType()!;
            var keys = Array.CreateInstance(keyType, 2); keys.SetValue(Enum.ToObject(keyType, 17), 0); keys.SetValue(Enum.ToObject(keyType, 39), 1);
            var input = Assert.IsAssignableFrom<Array>(method.Invoke(null, [keys]));
            Assert.Equal(4, input.Length);
            Assert.Equal((ushort)17, Key(input.GetValue(0)!, "Key")); Assert.Equal((ushort)39, Key(input.GetValue(1)!, "Key"));
            Assert.Equal(1u, Key(input.GetValue(1)!, "Flags")); Assert.Equal(3u, Key(input.GetValue(2)!, "Flags"));
            Assert.Equal((ushort)17, Key(input.GetValue(3)!, "Key")); Assert.Equal(2u, Key(input.GetValue(3)!, "Flags"));
        }
        finally { context.Unload(); }
    }
    private static (AssemblyLoadContext Context, Type Type) Load()
    {
        var directory = AppContext.BaseDirectory;
        var context = new AssemblyLoadContext("computer-input-" + Guid.NewGuid(), isCollectible: true);
        context.Resolving += (_, name) => File.Exists(Path.Combine(directory, name.Name + ".dll")) ? context.LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
        var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "AllAgent.NET.ComputerUse.dll"));
        return (context, assembly.GetType("AllAgent.NET.ComputerUse.NativeInput", throwOnError: true)!);
    }
    private static object Key(object input, string field)
    {
        var union = input.GetType().GetField("Data")!.GetValue(input)!;
        var keyboard = union.GetType().GetField("Keyboard")!.GetValue(union)!;
        return keyboard.GetType().GetField(field)!.GetValue(keyboard)!;
    }
}
