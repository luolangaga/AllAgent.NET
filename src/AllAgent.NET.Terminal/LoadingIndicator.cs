using System.Reflection;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal static class LoadingIndicator
{
    private static readonly IReadOnlyDictionary<string, Spinner> Catalog = typeof(Spinner.Known).GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(property => property.PropertyType == typeof(Spinner)).ToDictionary(property => property.Name, property => (Spinner)property.GetValue(null)!, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> Names { get; } = Catalog.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Name { get; private set; } = "Mindblown";
    public static Spinner Current => Catalog[Name];
    public static void Set(string? name)
    {
        name ??= "Mindblown";
        Name = Names.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("未知加载指示样式，使用 /spinner list 查看。");
    }
    public static string Frame(long milliseconds) => Current.Frames[(int)(milliseconds / Math.Max(1, Current.Interval.TotalMilliseconds) % Current.Frames.Count)];
    public static string DotsFrame(long milliseconds) => Spinner.Known.Dots.Frames[(int)(milliseconds / Math.Max(1, Spinner.Known.Dots.Interval.TotalMilliseconds) % Spinner.Known.Dots.Frames.Count)];
}
