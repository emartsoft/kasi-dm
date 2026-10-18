using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// Periodically snapshots the scheduler, flushes the part file, persists a changed segment structure and then records the
/// flushed cursors (TRD 6.5, ADR 0003, ADR 0008). The snapshot is taken before the flush; nothing is recorded when the flush
/// fails, so a stored cursor can never be ahead of the data on disk.
/// </summary>
internal sealed class SegmentCheckpointCoordinator
{
    private readonly JobId _jobId;
    private readonly ICheckpointWriter _checkpoints;
    private readonly ISegmentRepository _segments;
    private readonly PartFileWriter _writer;
    private readonly SegmentScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private int _persistedStructure = -1;

    public SegmentCheckpointCoordinator(
        JobId jobId,
        ICheckpointWriter checkpoints,
        ISegmentRepository segments,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        TimeProvider time,
        TimeSpan interval)
    {
        _jobId = jobId;
        _checkpoints = checkpoints;
        _segments = segments;
        _writer = writer;
        _scheduler = scheduler;
        _time = time;
        _interval = interval;
    }

    public Exception? Failure { get; private set; }

    public async Task SaveStructureAsync(CancellationToken cancellationToken)
    {
        var (snapshot, version) = _scheduler.GetCheckpointSnapshot();
        await _segments.ReplaceAsync(_jobId, snapshot, cancellationToken).ConfigureAwait(false);
        _persistedStructure = version;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The transfer ended; the caller runs the final checkpoint.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failure = ex;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var (snapshot, structure) = _scheduler.GetCheckpointSnapshot();
        var flush = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!flush.IsSuccess)
        {
            throw new SegmentCheckpointException(flush);
        }

        if (structure != _persistedStructure)
        {
            await _segments.ReplaceAsync(_jobId, snapshot, cancellationToken).ConfigureAwait(false);
            _persistedStructure = structure;
        }

        foreach (var segment in snapshot)
        {
            _checkpoints.Record(_jobId, segment.Index, segment.Written);
        }

        await _checkpoints.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SegmentCheckpointException(DiskStatus status) : Exception(status.Detail ?? "The segment checkpoint could not be written.")
{
    public DiskStatus Status { get; } = status;
}
