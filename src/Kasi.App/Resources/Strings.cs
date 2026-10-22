using System.Globalization;
using System.Resources;

namespace Kasi.App.Resources;

/// <summary>Typed access to <c>Strings.resx</c>; all user-visible text lives there (CONTRIBUTING 6, localisation-ready).</summary>
internal static class Strings
{
    private static readonly ResourceManager _resources = new("Kasi.App.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => Get(nameof(AppTitle));

    public static string MainWindowAutomationName => Get(nameof(MainWindowAutomationName));

    public static string SettingsTitle => Get(nameof(SettingsTitle));

    public static string SettingsAutomationName => Get(nameof(SettingsAutomationName));

    public static string SettingsGeneralTab => Get(nameof(SettingsGeneralTab));

    public static string SettingsBrowserTab => Get(nameof(SettingsBrowserTab));

    public static string SettingsGeneralPlaceholder => Get(nameof(SettingsGeneralPlaceholder));

    public static string BrowserInterceptionEnabled => Get(nameof(BrowserInterceptionEnabled));

    public static string BrowserExtensionsLabel => Get(nameof(BrowserExtensionsLabel));

    public static string BrowserMimeTypesLabel => Get(nameof(BrowserMimeTypesLabel));

    public static string BrowserExcludedSitesLabel => Get(nameof(BrowserExcludedSitesLabel));

    public static string BrowserBypassKeyLabel => Get(nameof(BrowserBypassKeyLabel));

    public static string BrowserMinSizeLabel => Get(nameof(BrowserMinSizeLabel));

    public static string BypassKeyAlt => Get(nameof(BypassKeyAlt));

    public static string BypassKeyCtrl => Get(nameof(BypassKeyCtrl));

    public static string BypassKeyShift => Get(nameof(BypassKeyShift));

    public static string BypassKeyNone => Get(nameof(BypassKeyNone));

    public static string SettingsSaveButton => Get(nameof(SettingsSaveButton));

    public static string SettingsCancelButton => Get(nameof(SettingsCancelButton));

    public static string SettingsSaveFailed => Get(nameof(SettingsSaveFailed));

    private static string Get(string name) => _resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
