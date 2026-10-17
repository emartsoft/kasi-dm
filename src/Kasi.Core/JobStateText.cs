namespace Kasi.Core;

/// <summary>The text stored in <c>job.state</c>. The strings are a storage format: changing one needs a migration.</summary>
public static class JobStateText
{
    public static string ToText(JobState state) => state switch
    {
        JobState.Queued => "Queued",
        JobState.Probing => "Probing",
        JobState.Downloading => "Downloading",
        JobState.Retrying => "Retrying",
        JobState.Paused => "Paused",
        JobState.Muxing => "Muxing",
        JobState.Verifying => "Verifying",
        JobState.Completed => "Completed",
        JobState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a defined job state."),
    };

    /// <summary>Exact, case-sensitive match; numbers and unknown text are rejected.</summary>
    public static bool TryParse(string? text, out JobState state)
    {
        (var found, state) = text switch
        {
            "Queued" => (true, JobState.Queued),
            "Probing" => (true, JobState.Probing),
            "Downloading" => (true, JobState.Downloading),
            "Retrying" => (true, JobState.Retrying),
            "Paused" => (true, JobState.Paused),
            "Muxing" => (true, JobState.Muxing),
            "Verifying" => (true, JobState.Verifying),
            "Completed" => (true, JobState.Completed),
            "Failed" => (true, JobState.Failed),
            _ => (false, default(JobState)),
        };
        return found;
    }
}
