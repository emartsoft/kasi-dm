namespace Kasi.Core;

/// <summary>Persistence for jobs. Implemented in <c>Kasi.Data</c>; defined here so the engine never references the data layer.</summary>
public interface IJobRepository
{
    /// <summary>Inserts the job; fails if the id exists or the queue or category does not.</summary>
    Task AddAsync(Job job, CancellationToken cancellationToken = default);

    Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>Jobs in queue order (queue, then <see cref="Job.QueuePos"/>), optionally limited to one queue and or one state.</summary>
    Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes every changeable column (all but id, kind and creation time). <see cref="Job.DoneBytes"/> is never lowered by this call,
    /// so a stale copy of a job cannot undo a checkpoint; use <see cref="ResetProgressAsync"/> for a restart. Returns false if there is no such job.
    /// </summary>
    Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes only what a probe learned (final URL, file name, size, validators, resumability) and leaves the state, error, progress
    /// and queue columns alone, so a caller that changed the state in the meantime (a user pause) is never overwritten with a stale
    /// copy. Returns false if there is no such job.
    /// </summary>
    Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Changes only the source and final URLs after a refreshed link is validated; preserves progress, validators and state.</summary>
    Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default);

    /// <summary>Sets the saved byte count back to zero, for a job that restarts from the beginning (TRD 6.5). Returns false if there is no such job.</summary>
    Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>Deletes the job and, by cascade, its segments and secrets. Returns false if there is no such job.</summary>
    Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set state transition. Changes the row only when it still holds <paramref name="from"/>; writes the
    /// error, the pause reason and the completion timestamp in the same statement, and never touches <c>done_bytes</c>.
    /// </summary>
    Task<bool> TryChangeStateAsync(
        JobId id,
        JobState fromState,
        JobState toState,
        string? failureMessage = null,
        PauseReason? pauseReason = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reconciles <c>done_bytes</c> with the sum of the segment cursors during recovery. Never used for normal progress.</summary>
    Task<bool> ReconcileProgressAsync(JobId id, long doneBytes, CancellationToken cancellationToken = default);

    /// <summary>Runs SQLite's <c>PRAGMA quick_check</c>; false means the database must not be used until repaired.</summary>
    Task<bool> QuickCheckAsync(CancellationToken cancellationToken = default);
}
