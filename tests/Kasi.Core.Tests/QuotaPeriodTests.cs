using Kasi.Core;

namespace Kasi.Core.Tests;

public sealed class QuotaPeriodTests
{
    [Fact]
    public void A_daily_period_starts_at_local_midnight_and_is_stored_in_utc()
    {
        var local = new DateTimeOffset(2026, 10, 25, 17, 30, 0, TimeSpan.FromHours(3));

        var start = QuotaPeriod.Start(QuotaReset.Daily, local);

        start.Should().Be(new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero));
        QuotaPeriod.Next(QuotaReset.Daily, local).Should().Be(new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_weekly_period_starts_on_local_monday()
    {
        // Sunday 2026-10-25 in the same +03:00 zone: the current week began on Monday the 19th.
        var local = new DateTimeOffset(2026, 10, 25, 17, 30, 0, TimeSpan.FromHours(3));

        QuotaPeriod.Start(QuotaReset.Weekly, local)
            .Should().Be(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_never_period_has_one_fixed_start_and_no_next_rollover()
    {
        var local = new DateTimeOffset(2026, 10, 25, 17, 30, 0, TimeSpan.Zero);

        QuotaPeriod.Start(QuotaReset.Never, local).Should().Be(DateTimeOffset.UnixEpoch);
        QuotaPeriod.Next(QuotaReset.Never, local).Should().BeNull();
    }
}
