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

    /// <summary>Sets the saved byte count back to zero, for a job that restarts from the beginning (TRD 6.5). Returns false if there is no such job.</summary>
    Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>Deletes the job and, by cascade, its segments and secrets. Returns false if there is no such job.</summary>
    Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default);
}
