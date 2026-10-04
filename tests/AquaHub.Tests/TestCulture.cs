using System.Globalization;
using System.Runtime.CompilerServices;

namespace AquaHub.Tests;

/// <summary>
/// AQUAHUB_TEST_CULTURE=th-TH runs the whole suite as on a PC set to that culture (CI runs it under Thai and Arabic
/// (Saudi) too: other calendars and number formats are where culture bugs hide).
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable("AQUAHUB_TEST_CULTURE") is not { Length: > 0 } name) return;
        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
