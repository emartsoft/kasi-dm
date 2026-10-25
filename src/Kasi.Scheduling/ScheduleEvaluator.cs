using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Evaluates whether a queue schedule is active at a given point in time,
/// correctly handling same-day windows, overnight windows crossing midnight,
/// and day-of-week bitmasks (TRD 10.2, KASI-QUE-003, TC-QUE-003, TC-QUE-007).
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>Determines whether the given schedule is currently active at <paramref name="now"/>.</summary>
    public static bool IsActive(QueueSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.IsEnabled)
        {
            return false;
        }

        var timeNow = TimeOnly.FromTimeSpan(now.TimeOfDay);

        // Case 1: No stop time -> starts at StartTime on enabled days and runs until the queue is empty.
        if (schedule.StopTime is null)
        {
            return IsDayEnabled(schedule.DaysOfWeek, now.DayOfWeek) && timeNow >= schedule.StartTime;
        }

        var stopTime = schedule.StopTime.Value;

        // Case 2: Same-day window (e.g. 09:00:00 to 17:00:00)
        if (schedule.StartTime < stopTime)
        {
            return IsDayEnabled(schedule.DaysOfWeek, now.DayOfWeek)
                   && timeNow >= schedule.StartTime
                   && timeNow < stopTime;
        }

        // Case 3: Overnight window crossing midnight (e.g. 23:00:00 to 06:00:00)
        // Part A: Late evening of the scheduled day (>= 23:00)
        if (timeNow >= schedule.StartTime)
        {
            return IsDayEnabled(schedule.DaysOfWeek, now.DayOfWeek);
        }

        // Part B: Early morning after the scheduled day (< 06:00)
        // Belongs to the overnight window that started yesterday evening.
        if (timeNow < stopTime)
        {
            var yesterday = now.AddDays(-1).DayOfWeek;
            return IsDayEnabled(schedule.DaysOfWeek, yesterday);
        }

        return false;
    }

    /// <summary>The next local start strictly after <paramref name="now"/>, or null when no day matches within two weeks.</summary>
    public static DateTimeOffset? NextStart(QueueSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.IsEnabled)
        {
            return null;
        }

        for (var day = 0; day <= 14; day++)
        {
            var date = now.Date.AddDays(day);
            if (!IsDayEnabled(schedule.DaysOfWeek, date.DayOfWeek))
            {
                continue;
            }

            var candidate = new DateTimeOffset(date + schedule.StartTime.ToTimeSpan(), now.Offset);
            if (candidate > now)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Checks whether the bit for <paramref name="day"/> is set in <paramref name="mask"/>.</summary>
    public static bool IsDayEnabled(DaysOfWeek mask, DayOfWeek day)
    {
        var bit = (DaysOfWeek)(1 << (int)day);
        return (mask & bit) != 0;
    }
}
