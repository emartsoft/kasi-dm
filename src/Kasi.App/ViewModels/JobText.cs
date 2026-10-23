using System.Globalization;
using System.Text;
using Kasi.App.Resources;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>The words and numbers of the job list, as people read them. Pure, so each rule is tested on its own.</summary>
internal static class JobText
{
    private static readonly CompositeFormat _seconds = CompositeFormat.Parse(Strings.TimeSeconds);
    private static readonly CompositeFormat _minutes = CompositeFormat.Parse(Strings.TimeMinutes);
    private static readonly CompositeFormat _hoursMinutes = CompositeFormat.Parse(Strings.TimeHoursMinutes);
    private static readonly CompositeFormat _days = CompositeFormat.Parse(Strings.TimeDays);
    private static readonly CompositeFormat _today = CompositeFormat.Parse(Strings.DateToday);
    private static readonly CompositeFormat _yesterday = CompositeFormat.Parse(Strings.DateYesterday);

    public static string State(JobState state) => state switch
    {
        JobState.Queued => Strings.StateQueued,
        JobState.Probing => Strings.StateProbing,
        JobState.Downloading => Strings.StateDownloading,
        JobState.Retrying => Strings.StateRetrying,
        JobState.Paused => Strings.StatePaused,
        JobState.Muxing => Strings.StateMuxing,
        JobState.Verifying => Strings.StateVerifying,
        JobState.Completed => Strings.StateCompleted,
        JobState.Failed => Strings.StateFailed,
        _ => state.ToString(),
    };

    /// <summary>Transferring, or finishing a transfer: what the "Active" filter and the active count mean.</summary>
    public static bool IsActive(JobState state) =>
        state is JobState.Downloading or JobState.Probing or JobState.Retrying or JobState.Muxing or JobState.Verifying;

    public static string Speed(long bytesPerSecond) => bytesPerSecond <= 0 ? string.Empty : ByteText.Format(bytesPerSecond) + "/s";

    /// <summary>"45 s", "4 min", "2 h 5 min", "3 d"; empty when the time is not known.</summary>
    public static string TimeLeft(long? seconds)
    {
        if (seconds is not { } total || total < 0)
        {
            return string.Empty;
        }

        var culture = CultureInfo.CurrentCulture;
        return total switch
        {
            < 60 => string.Format(culture, _seconds, total),
            < 3600 => string.Format(culture, _minutes, (total + 30) / 60),
            < 86400 => string.Format(culture, _hoursMinutes, total / 3600, total % 3600 / 60),
            _ => string.Format(culture, _days, total / 86400),
        };
    }

    /// <summary>"Today 09:12", "Yesterday 22:10", "Mon 14:22" within the last week, else the short date.</summary>
    public static string DateAdded(DateTimeOffset created, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = created.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", culture);
        if (local.Date == today)
        {
            return string.Format(culture, _today, time);
        }

        if (local.Date == today.AddDays(-1))
        {
            return string.Format(culture, _yesterday, time);
        }

        return (today - local.Date).TotalDays < 7
            ? local.ToString("ddd ", culture) + time
            : local.ToString("d", culture);
    }
}
