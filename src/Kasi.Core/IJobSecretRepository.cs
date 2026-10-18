namespace Kasi.Core;

/// <summary>Stores per-job secrets encrypted for the current Windows user (TRD 9.4, 11.2).</summary>
public interface IJobSecretRepository
{
    /// <summary>Inserts or replaces the secret; a missing job is an error.</summary>
    Task SetAsync(JobId id, JobSecret secret, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the job has no saved secret.</summary>
    Task<JobSecret?> GetAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret, returning false when there was none.</summary>
    Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default);
}
