using Avalonia;
using Avalonia.Styling;

namespace Kasi.App.Engine;

/// <summary>Turns the theme preference into what Avalonia understands (KASI-UI-006): Default follows the Windows setting, live.</summary>
internal static class ThemeApplier
{
    public static ThemeVariant Variant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static void Apply(Application application, ThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.RequestedThemeVariant = Variant(preference);
    }
}
