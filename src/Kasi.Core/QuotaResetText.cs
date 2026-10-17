namespace Kasi.Core;

/// <summary>The text stored in <c>queue.quota_reset</c>; the column is NULL when there is no quota.</summary>
public static class QuotaResetText
{
    public static string ToText(QuotaReset reset) => reset switch
    {
        QuotaReset.Daily => "daily",
        QuotaReset.Weekly => "weekly",
        QuotaReset.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(reset), reset, "Not a defined quota reset."),
    };

    public static bool TryParse(string? text, out QuotaReset reset)
    {
        (var found, reset) = text switch
        {
            "daily" => (true, QuotaReset.Daily),
            "weekly" => (true, QuotaReset.Weekly),
            "never" => (true, QuotaReset.Never),
            _ => (false, default(QuotaReset)),
        };
        return found;
    }
}
