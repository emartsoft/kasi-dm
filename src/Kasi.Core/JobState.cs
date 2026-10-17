namespace Kasi.Core;

/// <summary>The nine job states of TRD 6.9. Persisted as text through <see cref="JobStateText"/>, never as numbers.</summary>
public enum JobState
{
    Queued,
    Probing,
    Downloading,
    Retrying,
    Paused,
    Muxing,
    Verifying,
    Completed,
    Failed,
}
