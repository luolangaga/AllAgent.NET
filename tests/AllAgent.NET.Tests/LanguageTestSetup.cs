using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Localization;

namespace AllAgent.NET.Tests;

internal static class LanguageTestSetup
{
    /// <summary>Existing assertions target the Chinese catalog; keep runs deterministic on any OS locale.</summary>
    [ModuleInitializer]
    internal static void Initialize() => HostText.Set(UiLanguage.Zh);
}
