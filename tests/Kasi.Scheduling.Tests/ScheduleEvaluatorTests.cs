using Kasi.Core;
using Kasi.Scheduling;

namespace Kasi.Scheduling.Tests;

public sealed class ScheduleEvaluatorTests
{
    [Fact]
    public void Disabled_schedule_is_never_active()
    {
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = false,
            StartTime = new TimeOnly(9, 0),
            StopTime = new TimeOnly(17, 0),
            DaysOfWeek = DaysOfWeek.All,
        };

        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero); // Friday 12:00
        ScheduleEvaluator.IsActive(schedule, now).Should().BeFalse();
    }

    [Fact]
    public void Same_day_window_is_active_only_between_start_and_stop()
    {
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(9, 0),
            StopTime = new TimeOnly(17, 0),
            DaysOfWeek = DaysOfWeek.All,
        };

        var before = new DateTimeOffset(2026, 10, 23, 8, 59, 59, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var middle = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 23, 17, 0, 1, TimeSpan.Zero);

        ScheduleEvaluator.IsActive(schedule, before).Should().BeFalse();
        ScheduleEvaluator.IsActive(schedule, start).Should().BeTrue();
        ScheduleEvaluator.IsActive(schedule, middle).Should().BeTrue();
        ScheduleEvaluator.IsActive(schedule, end).Should().BeFalse();
        ScheduleEvaluator.IsActive(schedule, after).Should().BeFalse();
    }

    [Fact]
    public void Overnight_window_crossing_midnight_evaluates_correctly()
    {
        // 23:00 to 06:00, enabled on Friday only
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            StopTime = new TimeOnly(6, 0),
            DaysOfWeek = DaysOfWeek.Friday,
        };

        // Friday 22:59 -> not active yet
        var friEveningBefore = new DateTimeOffset(2026, 10, 23, 22, 59, 0, TimeSpan.Zero); // Friday
        ScheduleEvaluator.IsActive(schedule, friEveningBefore).Should().BeFalse();

        // Friday 23:00 -> active!
        var friEveningStart = new DateTimeOffset(2026, 10, 23, 23, 0, 0, TimeSpan.Zero);
        ScheduleEvaluator.IsActive(schedule, friEveningStart).Should().BeTrue();

        // Saturday 03:00 -> active! (belongs to Friday overnight schedule)
        var satEarlyMorning = new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero); // Saturday
        ScheduleEvaluator.IsActive(schedule, satEarlyMorning).Should().BeTrue();

        // Saturday 05:59:59 -> active!
        var satBeforeEnd = new DateTimeOffset(2026, 10, 24, 5, 59, 59, TimeSpan.Zero);
        ScheduleEvaluator.IsActive(schedule, satBeforeEnd).Should().BeTrue();

        // Saturday 06:00:00 -> window closed
        var satEnd = new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);
        ScheduleEvaluator.IsActive(schedule, satEnd).Should().BeFalse();

        // Saturday 23:30 -> Saturday night is NOT enabled!
        var satNight = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        ScheduleEvaluator.IsActive(schedule, satNight).Should().BeFalse();

        // Sunday 03:00 -> Sunday morning is NOT enabled!
        var sunMorning = new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        ScheduleEvaluator.IsActive(schedule, sunMorning).Should().BeFalse();
    }

    [Fact]
    public void Open_ended_schedule_with_no_stop_time_starts_at_start_time_on_enabled_days()
    {
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(14, 0),
            StopTime = null,
            DaysOfWeek = DaysOfWeek.Friday,
        };

        var before = new DateTimeOffset(2026, 10, 23, 13, 59, 0, TimeSpan.Zero); // Friday
        var after = new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.Zero); // Friday
        var wrongDay = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero); // Saturday

        ScheduleEvaluator.IsActive(schedule, before).Should().BeFalse();
        ScheduleEvaluator.IsActive(schedule, after).Should().BeTrue();
        ScheduleEvaluator.IsActive(schedule, wrongDay).Should().BeFalse();
    }
}
