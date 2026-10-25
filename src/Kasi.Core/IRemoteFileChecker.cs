namespace Kasi.Core;

/// <summary>
/// Checks whether a remote resource has changed compared to a job's recorded metadata (TRD 10.1, KASI-QUE-006).
/// </summary>
public interface IRemoteFileChecker
{
    /// <summary>
    /// Probes the remote URL for <paramref name="job"/> and returns true if its ETag, Last-Modified or size
    /// differs from what was recorded when the job previously completed, or false if it is unchanged.
    /// </summary>
    Task<bool> HasRemoteChangedAsync(Job job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares the engine to overwrite the existing destination file for <paramref name="jobId"/> when re-downloaded.
    /// </summary>
    void PrepareForRedownload(JobId jobId);
}
