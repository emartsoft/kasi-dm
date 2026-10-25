namespace Kasi.Core;

/// <summary>The start of a queue quota period (KASI-QUE-007). Windows are local: daily at local midnight, weekly at local Monday 00:00.</summary>
public static class QuotaPeriod
{
    public static DateTimeOffset Start(QuotaReset reset, DateTimeOffset localNow)
    {
        var date = localNow.Date;
        var period = reset switch
        {
            QuotaReset.Daily => date,
            QuotaReset.Weekly => date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7)),
            QuotaReset.Never => DateTimeOffset.UnixEpoch.DateTime,
            _ => throw new ArgumentOutOfRangeException(nameof(reset), reset, "Not a defined quota reset."),
        };
        return reset == QuotaReset.Never
            ? DateTimeOffset.UnixEpoch
            : new DateTimeOffset(period, localNow.Offset).ToUniversalTime();
    }

    /// <summary>The next local rollover, or null for <see cref="QuotaReset.Never"/>.</summary>
    public static DateTimeOffset? Next(QuotaReset reset, DateTimeOffset localNow) => reset switch
    {
        QuotaReset.Daily => new DateTimeOffset(localNow.Date.AddDays(1), localNow.Offset).ToUniversalTime(),
        QuotaReset.Weekly => new DateTimeOffset(
            localNow.Date.AddDays(7 - (((int)localNow.DayOfWeek + 6) % 7)),
            localNow.Offset).ToUniversalTime(),
        QuotaReset.Never => null,
        _ => throw new ArgumentOutOfRangeException(nameof(reset), reset, "Not a defined quota reset."),
    };
}
