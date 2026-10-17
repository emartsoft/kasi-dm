namespace Kasi.Core;

/// <summary>Persistence for the byte ranges of a job. Implemented in <c>Kasi.Data</c>.</summary>
public interface ISegmentRepository
{
    /// <summary>The job's segments ordered by index; empty when it has none.</summary>
    Task<IReadOnlyList<Segment>> LoadAsync(JobId jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces all of the job's segments in one transaction and sets the job's saved byte count to the sum of their written bytes,
    /// so a restart (new segments, nothing written) resets progress atomically. If anything fails the old segments stay.
    /// Per-cycle progress goes through <see cref="ICheckpointWriter"/>, not through here.
    /// </summary>
    Task ReplaceAsync(JobId jobId, IReadOnlyList<Segment> segments, CancellationToken cancellationToken = default);
}
