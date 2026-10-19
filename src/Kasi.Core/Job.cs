namespace Kasi.Core;

/// <summary>
/// One download, capture or torrent: the columns of the <c>job</c> table (TRD 11.2). Immutable; the only way to change
/// the state is <see cref="TryTransition"/>, which applies <see cref="JobStateMachine"/>.
/// </summary>
public sealed record Job
{
    public required JobId Id { get; init; }

    public required JobKind Kind { get; init; }

    public required string Url { get; init; }

    public string? FinalUrl { get; init; }

    public string? Referrer { get; init; }

    public required string FileName { get; init; }

    public required string Folder { get; init; }

    /// <summary>Null while the size is unknown.</summary>
    public long? TotalBytes { get; init; }

    public long DoneBytes { get; init; }

    public JobState State { get; init; } = JobState.Queued;

    public string? Error { get; init; }

    /// <summary>Why the job was paused; null in every other state. Persisted so a restart can auto-resume only network pauses.</summary>
    public PauseReason? PauseReason { get; init; }

    /// <summary>How many times a changed remote file forced a restart; capped by the facade (KASI-RES-003).</summary>
    public int RestartCount { get; init; }

    public string? ETag { get; init; }

    /// <summary>The <c>Last-Modified</c> header exactly as the server sent it, so <c>If-Range</c> can send it back unchanged.</summary>
    public string? LastModified { get; init; }

    public bool Resumable { get; init; } = true;

    public int? Connections { get; init; }

    public long? SpeedLimitBps { get; init; }

    public string? ChecksumAlgo { get; init; }

    public string? Checksum { get; init; }

    public required int QueueId { get; init; }

    /// <summary>Fractional index, so a job can be moved between two neighbours without renumbering the rest.</summary>
    public required double QueuePos { get; init; }

    public int? CategoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>
    /// Moves to <paramref name="to"/> if <see cref="JobStateMachine"/> allows it, otherwise returns false and leaves
    /// <paramref name="updated"/> equal to this job. Failing needs a message; retrying may carry the last transient error;
    /// leaving Failed or Retrying clears it. Completing stamps <see cref="CompletedUtc"/>.
    /// </summary>
    public bool TryTransition(JobState to, TimeProvider time, out Job updated, string? error = null)
    {
        if (to == JobState.Failed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
        }

        if (!JobStateMachine.CanTransition(Kind, State, to))
        {
            updated = this;
            return false;
        }

        updated = this with
        {
            State = to,
            Error = to is JobState.Failed or JobState.Retrying ? error : null,
            CompletedUtc = to == JobState.Completed ? time.GetUtcNow() : null,
        };
        return true;
    }
}
