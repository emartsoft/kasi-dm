using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>The quota side of the runner: reservations before a start and actual bytes from the engine (KASI-QUE-007).</summary>
public interface IQueueQuota
{
    event EventHandler<int>? QuotaReached;

    /// <summary>Reserves the known remaining size of a job. False means the queue is at its cap and the job must not start.</summary>
    Task<bool> TryReserveAsync(Job job, Queue queue, CancellationToken cancellationToken = default);

    Task ReleaseAsync(JobId jobId, CancellationToken cancellationToken = default);

    Task<bool> HasCapacityAsync(int queueId, CancellationToken cancellationToken = default);
}
