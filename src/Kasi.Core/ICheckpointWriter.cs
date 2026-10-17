namespace Kasi.Core;

/// <summary>
/// Records how much of each segment is safely on disk (TRD 6.5, 11.3). The contract that makes resume correct: a worker calls
/// <see cref="Record"/> only <b>after</b> <c>FlushFileBuffers</c> on the part file has returned, never before.
/// </summary>
public interface ICheckpointWriter
{
    /// <summary>
    /// Notes that <paramref name="written"/> bytes from the start of the segment are flushed. Cheap and non-blocking; the
    /// value is committed with the next batch. A smaller value than one already recorded never moves progress backwards.
    /// </summary>
    void Record(JobId jobId, int segmentIndex, long written);

    /// <summary>
    /// Commits everything recorded so far and completes once it is in the database. Call it, after flushing the files, on pause and
    /// on shutdown. Throws if the commit fails; the updates are kept and retried either way.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
