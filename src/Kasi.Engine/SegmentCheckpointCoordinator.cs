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
    private readonly int _maxConsecutiveFailures;
    private readonly Action<Exception>? _onPersistentFailure;
    private int _persistedStructure = -1;

    public SegmentCheckpointCoordinator(
        JobId jobId,
        ICheckpointWriter checkpoints,
        ISegmentRepository segments,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        TimeProvider time,
        TimeSpan interval,
        int maxConsecutiveFailures = DefaultMaxConsecutiveFailures,
        Action<Exception>? onPersistentFailure = null)
    {
        _jobId = jobId;
        _checkpoints = checkpoints;
        _segments = segments;
        _writer = writer;
        _scheduler = scheduler;
        _time = time;
        _interval = interval;
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _onPersistentFailure = onPersistentFailure;
    }

    /// <summary>Consecutive failing ticks before the transfer is stopped (ADR 0003, ADR 0008): about three intervals of no durable record.</summary>
    public const int DefaultMaxConsecutiveFailures = 3;

    /// <summary>The failure that stopped the periodic checkpoint, or null; a <see cref="SegmentCheckpointException"/> carries a disk status.</summary>
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
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A part file that cannot be flushed (disk full, I/O error) means durability cannot be established at all, so
                    // it stops the transfer at once. A database hiccup (SQLite busy because of an indexer or antivirus) is
                    // tolerated until it has failed on several ticks in a row, and a good tick starts the count again.
                    failures++;
                    if (ex is SegmentCheckpointException || failures >= _maxConsecutiveFailures)
                    {
                        Failure = ex;
                        _onPersistentFailure?.Invoke(ex);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The transfer ended; the caller runs the final checkpoint.
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
