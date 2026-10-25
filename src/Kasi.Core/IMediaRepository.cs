namespace Kasi.Core;

/// <summary>Persistence for a media capture's streams and segments (TRD 7.4, 11.2; implemented in Kasi.Data).</summary>
public interface IMediaRepository
{
    /// <summary>Replaces the stream/segment structure in one transaction and resets the job's saved progress.</summary>
    Task ReplaceAsync(
        JobId jobId,
        IReadOnlyList<MediaStreamRecord> streams,
        IReadOnlyList<MediaSegmentRecord> segments,
        long? estimatedTotalBytes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaStreamRecord>> LoadStreamsAsync(JobId jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaSegmentRecord>> LoadSegmentsAsync(JobId jobId, CancellationToken cancellationToken = default);

    /// <summary>Appends newly observed live segments without resetting completed rows or job progress.</summary>
    Task AppendSegmentsAsync(JobId jobId, IReadOnlyList<MediaSegmentRecord> segments,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a batch of segments complete and updates the job's done/total bytes in one transaction.</summary>
    Task MarkCompletedAsync(
        JobId jobId,
        IReadOnlyList<MediaSegmentCompletion> completed,
        long? estimatedTotalBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Clears completion flags and the job's progress but keeps the rows and segment URLs for a restart.</summary>
    Task ResetCompletionAsync(JobId jobId, CancellationToken cancellationToken = default);
}
