using System.Runtime.CompilerServices;
using Petal.App.Localization;

namespace Petal.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Tests expect English interface text whatever the PC's display language; the
    /// localization tests switch to Arabic themselves.
    /// </summary>
    [ModuleInitializer]
    internal static void UseEnglish() => L.Configure(UiLanguage.English);
}
