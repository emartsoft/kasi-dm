using System.Globalization;

namespace Kasi.App.ViewModels;

/// <summary>Sizes and speeds as people read them: 1,536 becomes 1.5 KB.</summary>
internal static class ByteText
{
    private static readonly string[] _units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < _units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + _units[unit];
    }
}
