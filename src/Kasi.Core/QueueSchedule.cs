namespace Kasi.Core;

/// <summary>Days of week bitmask for schedules (TRD 10.2). Values match <c>1 &lt;&lt; (int)DayOfWeek</c>.</summary>
[Flags]
public enum DaysOfWeek
{
    None = 0,
    Sunday = 1 << DayOfWeek.Sunday,       // 1
    Monday = 1 << DayOfWeek.Monday,       // 2
    Tuesday = 1 << DayOfWeek.Tuesday,     // 4
    Wednesday = 1 << DayOfWeek.Wednesday, // 8
    Thursday = 1 << DayOfWeek.Thursday,   // 16
    Friday = 1 << DayOfWeek.Friday,       // 32
    Saturday = 1 << DayOfWeek.Saturday,   // 64
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday, // 127
}

/// <summary>Daily or overnight operating window and days of week for a queue (TRD 10.2, KASI-QUE-003).</summary>
public sealed record QueueSchedule
{
    public required int QueueId { get; init; }

    public bool IsEnabled { get; init; }

    /// <summary>Daily start time of the queue (e.g. 23:00:00).</summary>
    public required TimeOnly StartTime { get; init; }

    /// <summary>Optional stop time (e.g. 06:00:00). Null means run until the queue is empty.</summary>
    public TimeOnly? StopTime { get; init; }

    /// <summary>Days of the week when this schedule is active.</summary>
    public DaysOfWeek DaysOfWeek { get; init; } = DaysOfWeek.All;

    /// <summary>The speed profile applied while this window is active; null leaves the manual limit alone.</summary>
    public int? ProfileId { get; init; }
}
