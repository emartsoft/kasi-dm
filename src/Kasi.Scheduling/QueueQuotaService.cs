using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Keeps a queue's quota period total, reserves known remaining bytes before a job starts and raises <see cref="QuotaReached"/> when
/// the soft cap is hit (KASI-QUE-007, TC-QUE-006). The cap is soft by design: an unknown-length transfer can overshoot by one write
/// chunk per active connection, while known ranges and media segments are reserved in full.
/// </summary>
public sealed class QueueQuotaService : IQueueQuota, IJobByteUsageSink, IDisposable
{
    private readonly IQueueRepository _queues;
    private readonly IJobRepository _jobs;
    private readonly IQueueUsageRepository _usage;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<JobId, (int QueueId, long Remaining)> _reserved = [];
    private readonly Dictionary<int, long> _reservedByQueue = [];
    private readonly HashSet<(int QueueId, DateTimeOffset Period)> _reached = [];

    public QueueQuotaService(
        IQueueRepository queues,
        IJobRepository jobs,
        IQueueUsageRepository usage,
        TimeProvider? timeProvider = null)
    {
        _queues = queues;
        _jobs = jobs;
        _usage = usage;
        _time = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<int>? QuotaReached;

    public async Task<bool> TryReserveAsync(Job job, Queue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.QuotaBytes is not { } cap || cap <= 0)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reserved.Remove(job.Id, out var old))
            {
                _reservedByQueue[old.QueueId] = Math.Max(0, _reservedByQueue.GetValueOrDefault(old.QueueId) - old.Remaining);
            }

            var period = QuotaPeriod.Start(queue.QuotaReset ?? QuotaReset.Never, _time.GetLocalNow());
            var used = await _usage.GetAsync(queue.Id, period, cancellationToken).ConfigureAwait(false);
            var reserved = _reservedByQueue.GetValueOrDefault(queue.Id);
            if (used + reserved >= cap)
            {
                RaiseIfNeeded(queue.Id, period);
                return false;
            }

            var remaining = Math.Max(0, (job.TotalBytes ?? job.DoneBytes) - job.DoneBytes);
            if (used + reserved + remaining > cap)
            {
                return false;
            }

            if (remaining > 0)
            {
                _reserved[job.Id] = (queue.Id, remaining);
                _reservedByQueue[queue.Id] = reserved + remaining;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reserved.Remove(jobId, out var entry))
            {
                _reservedByQueue[entry.QueueId] = Math.Max(0, _reservedByQueue.GetValueOrDefault(entry.QueueId) - entry.Remaining);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasCapacityAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var queue = await _queues.GetAsync(queueId, cancellationToken).ConfigureAwait(false);
        if (queue?.QuotaBytes is not { } cap || cap <= 0)
        {
            return true;
        }

        var period = QuotaPeriod.Start(queue.QuotaReset ?? QuotaReset.Never, _time.GetLocalNow());
        var used = await _usage.GetAsync(queueId, period, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return used + _reservedByQueue.GetValueOrDefault(queueId) < cap;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Report(JobId jobId, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        _ = ApplyAsync(jobId, bytes);
    }

    private async Task ApplyAsync(JobId jobId, long bytes)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                int queueId;
                long remaining = 0;
                if (_reserved.TryGetValue(jobId, out var entry))
                {
                    queueId = entry.QueueId;
                    remaining = entry.Remaining;
                }
                else
                {
                    var job = await _jobs.GetAsync(jobId).ConfigureAwait(false);
                    if (job is null)
                    {
                        return;
                    }

                    queueId = job.QueueId;
                }

                var queue = await _queues.GetAsync(queueId).ConfigureAwait(false);
                if (queue?.QuotaBytes is not { } cap || cap <= 0)
                {
                    return;
                }

                var period = QuotaPeriod.Start(queue.QuotaReset ?? QuotaReset.Never, _time.GetLocalNow());
                var total = await _usage.AddAsync(queueId, period, bytes).ConfigureAwait(false);
                if (remaining > 0)
                {
                    var consumed = Math.Min(remaining, bytes);
                    var left = remaining - consumed;
                    if (left == 0)
                    {
                        _reserved.Remove(jobId);
                        _reservedByQueue[queueId] = Math.Max(0, _reservedByQueue.GetValueOrDefault(queueId) - remaining);
                    }
                    else
                    {
                        _reserved[jobId] = (queueId, left);
                        _reservedByQueue[queueId] = Math.Max(0, _reservedByQueue.GetValueOrDefault(queueId) - consumed);
                    }
                }

                if (total >= cap)
                {
                    RaiseIfNeeded(queueId, period);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // Accounting must never take a transfer down; the next report and the queue pump try again.
        }
    }

    private void RaiseIfNeeded(int queueId, DateTimeOffset period)
    {
        if (_reached.Add((queueId, period)))
        {
            QuotaReached?.Invoke(this, queueId);
        }
    }

    public void Dispose() => _gate.Dispose();
}
