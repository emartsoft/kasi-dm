using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Kasi.App.ViewModels;

/// <summary>Headings in the sidebar are small and bold; the entries under them are ordinary.</summary>
internal static class SidebarFonts
{
    public static readonly IValueConverter Weight = new FuncValueConverter<bool, FontWeight>(isHeader => isHeader ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter Opacity = new FuncValueConverter<bool, double>(isHeader => isHeader ? 0.65 : 1.0);
}
