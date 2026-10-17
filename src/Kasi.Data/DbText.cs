using System.Globalization;

namespace Kasi.Data;

/// <summary>How dates are stored: UTC ISO-8601 with 100 ns precision, which also sorts correctly as text.</summary>
internal static class DbText
{
    public static string FromDate(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string? FromDate(DateTimeOffset? value) => value is { } v ? FromDate(v) : null;

    public static DateTimeOffset ToDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    public static DateTimeOffset? ToDateOrNull(string? text) => text is null ? null : ToDate(text);
}
