namespace Kasi.Core;

/// <summary>Persists per-queue data usage for quota periods (TRD 10.1, KASI-QUE-007).</summary>
public interface IQueueUsageRepository
{
    Task<long> GetAsync(int queueId, DateTimeOffset periodStart, CancellationToken cancellationToken = default);

    /// <summary>Adds bytes to the period and returns the new total.</summary>
    Task<long> AddAsync(int queueId, DateTimeOffset periodStart, long bytes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueUsage>> ListAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(int queueId, DateTimeOffset periodStart, CancellationToken cancellationToken = default);
}
