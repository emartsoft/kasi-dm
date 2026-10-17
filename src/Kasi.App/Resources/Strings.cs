using System.Globalization;
using System.Resources;

namespace Kasi.App.Resources;

/// <summary>Typed access to <c>Strings.resx</c>; all user-visible text lives there (CONTRIBUTING 6, localisation-ready).</summary>
internal static class Strings
{
    private static readonly ResourceManager _resources = new("Kasi.App.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => Get(nameof(AppTitle));

    public static string MainWindowAutomationName => Get(nameof(MainWindowAutomationName));

    private static string Get(string name) => _resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
