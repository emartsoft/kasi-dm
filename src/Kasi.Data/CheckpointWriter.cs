using System.Threading.Channels;
using Dapper;
using Kasi.Core;

namespace Kasi.Data;

/// <summary>
/// The single writer of segment progress (TRD 11.3). Workers hand it flushed cursors through <see cref="Record"/>; one background
/// reader collects them and commits all of them in one transaction every interval (2 s by default), on <see cref="FlushAsync"/>, and
/// when it is stopped. Keeping SQLite to one writer keeps each commit well under a millisecond at 20 active jobs.
/// A failed commit keeps the updates and tries again on the next tick; nothing is dropped.
/// </summary>
public sealed class CheckpointWriter : ICheckpointWriter, IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<(JobId Job, int Segment), long> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private PeriodicTimer? _timer;
    private Task? _ticker;
    private Task? _reader;
    private int _commits;
    private int _faults;
    private int _disposed;
    private volatile Exception? _lastError;

    public CheckpointWriter(SqliteConnectionFactory factory, TimeProvider? timeProvider = null, TimeSpan? interval = null)
    {
        _factory = factory;
        _time = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Transactions committed so far.</summary>
    public int CommitCount => Volatile.Read(ref _commits);

    /// <summary>Commit attempts that failed; the updates they carried are still pending.</summary>
    public int FaultCount => Volatile.Read(ref _faults);

    /// <summary>The error of the latest failed commit, or null once a later commit succeeded.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Starts the background tasks. Updates recorded before this are kept and committed once it runs.</summary>
    public void Start()
    {
        if (_reader is not null)
        {
            return;
        }

        // Created here, not inside the ticker task: the first tick is due one interval after Start returns, however
        // long the thread pool takes to start the task. (A timer made later would measure from the wrong moment.)
        _timer = new PeriodicTimer(_interval, _time);
        _reader = Task.Run(ReadAsync);
        _ticker = Task.Run(TickAsync);
    }

    public void Record(JobId jobId, int segmentIndex, long written)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(written);
        var accepted = _channel.Writer.TryWrite(new Update(jobId, segmentIndex, written));
        ObjectDisposedException.ThrowIf(!accepted, this); // only after DisposeAsync: stop the workers first
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = _channel.Writer.TryWrite(new Flush(waiter));
        ObjectDisposedException.ThrowIf(!accepted, this);

        await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the ticker, commits what is left and ends the reader. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // both tasks were started by this class in Start
        if (_ticker is not null)
        {
            await _ticker.ConfigureAwait(false);
        }

        _channel.Writer.TryComplete();
        if (_reader is not null)
        {
            await _reader.ConfigureAwait(false);
        }
#pragma warning restore VSTHRD003

        _timer?.Dispose();
        _stop.Dispose();
    }

    private async Task TickAsync()
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                _channel.Writer.TryWrite(Tick.Instance);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task ReadAsync()
    {
        await foreach (var message in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (message)
            {
                case Update update:
                    var key = (update.Job, update.Segment);
                    _pending[key] = _pending.TryGetValue(key, out var known) ? Math.Max(known, update.Written) : update.Written;
                    break;
                case Tick:
                    await TryCommitAsync().ConfigureAwait(false);
                    break;
                case Flush flush:
                    var failure = await TryCommitAsync().ConfigureAwait(false);
                    if (failure is null)
                    {
                        flush.Waiter.SetResult();
                    }
                    else
                    {
                        flush.Waiter.SetException(failure);
                    }

                    break;
            }
        }

        await TryCommitAsync().ConfigureAwait(false);
    }

    /// <summary>Commits the pending updates; returns null on success (or nothing to do) and the error otherwise, keeping the updates.</summary>
    private async Task<Exception?> TryCommitAsync()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        try
        {
            using var connection = await _factory.OpenAsync().ConfigureAwait(false);
            using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            foreach (var ((job, segment), written) in _pending)
            {
                // never backwards, never past the end of the range; an update for a job or segment that is gone changes no rows
                await connection.ExecuteAsync(
                    "UPDATE segment SET written = MIN(MAX(written, @written), \"end\" - start) WHERE job_id = @job AND idx = @segment;",
                    new { job = job.ToString(), segment, written },
                    transaction).ConfigureAwait(false);
            }

            foreach (var job in _pending.Keys.Select(k => k.Job).Distinct())
            {
                await connection.ExecuteAsync(
                    "UPDATE job SET done_bytes = (SELECT COALESCE(SUM(written), 0) FROM segment WHERE job_id = @job) WHERE id = @job;",
                    new { job = job.ToString() },
                    transaction).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _faults);
            _lastError = ex;
            return ex;
        }

        _pending.Clear();
        _lastError = null;
        Interlocked.Increment(ref _commits);
        return null;
    }

    private abstract record Message;

    private sealed record Update(JobId Job, int Segment, long Written) : Message;

    private sealed record Flush(TaskCompletionSource Waiter) : Message;

    private sealed record Tick : Message
    {
        public static readonly Tick Instance = new();
    }
}
