using System.Collections.Concurrent;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Enforces per-queue concurrency limits (<see cref="Queue.MaxParallel"/>), dispatches queued jobs,
/// shifts failing jobs to queue tails, and manages fractional-index job reordering (TRD 10.1, KASI-QUE-001, KASI-QUE-002).
/// </summary>
public sealed class QueueRunner(
    IQueueRepository queueRepository,
    IJobRepository jobRepository,
    IJobControl jobControl) : IQueueRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, bool> _runningQueues = new();
    private readonly ConcurrentDictionary<JobId, int> _activeJobs = new();
    private readonly ConcurrentDictionary<int, bool> _ranSinceStart = new();

    public event EventHandler<int>? QueueCompleted;

    public bool IsQueueRunning(int queueId) =>
        _runningQueues.TryGetValue(queueId, out var running) && running;

    public async Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default)
    {
        _ranSinceStart.TryRemove(queueId, out _);
        _runningQueues[queueId] = true;
        await PumpQueueAsync(queueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default)
    {
        _runningQueues[queueId] = false;

        List<JobId> toPause;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            toPause = [.. _activeJobs.Where(kvp => kvp.Value == queueId).Select(kvp => kvp.Key)];
        }
        finally
        {
            _gate.Release();
        }

        foreach (var id in toPause)
        {
            await jobControl.PauseJobAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PumpQueueAsync(int queueId, CancellationToken cancellationToken = default)
    {
        if (!IsQueueRunning(queueId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PumpQueueUnderLockAsync(queueId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnJobStateChangedAsync(JobId jobId, JobState newState, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (newState is JobState.Completed or JobState.Failed or JobState.Paused)
            {
                _activeJobs.TryRemove(jobId, out var queueId);
                if (queueId == 0)
                {
                    var job = await jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
                    if (job is not null)
                    {
                        queueId = job.QueueId;
                    }
                }

                if (queueId > 0 && newState != JobState.Paused)
                {
                    _ranSinceStart[queueId] = true;
                }

                if (newState == JobState.Failed && queueId > 0)
                {
                    var allJobs = await jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    double maxPos = allJobs.Count > 0 ? allJobs.Max(j => j.QueuePos) : 0.0;
                    double tailPos = FractionalIndex.After(maxPos);
                    await jobRepository.UpdateQueuePositionAsync(jobId, queueId, tailPos, cancellationToken).ConfigureAwait(false);
                }

                if (queueId > 0 && IsQueueRunning(queueId))
                {
                    await PumpQueueUnderLockAsync(queueId, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (IsActiveState(newState))
            {
                var job = await jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (job is not null)
                {
                    _activeJobs[jobId] = job.QueueId;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReorderJobAsync(JobId jobId, JobId? beforeJobId, JobId? afterJobId, CancellationToken cancellationToken = default)
    {
        if (jobId == beforeJobId || jobId == afterJobId)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return false;
            }

            var beforeJob = beforeJobId is { } bId ? await jobRepository.GetAsync(bId, cancellationToken).ConfigureAwait(false) : null;
            var afterJob = afterJobId is { } aId ? await jobRepository.GetAsync(aId, cancellationToken).ConfigureAwait(false) : null;

            if (beforeJobId is not null && beforeJob is null)
            {
                return false;
            }

            if (afterJobId is not null && afterJob is null)
            {
                return false;
            }

            double newPos;
            if (beforeJob is not null && afterJob is not null)
            {
                if (FractionalIndex.NeedsRebalance(beforeJob.QueuePos, afterJob.QueuePos))
                {
                    await RebalanceQueueUnderLockAsync(job.QueueId, cancellationToken).ConfigureAwait(false);
                    beforeJob = await jobRepository.GetAsync(beforeJob.Id, cancellationToken).ConfigureAwait(false);
                    afterJob = await jobRepository.GetAsync(afterJob.Id, cancellationToken).ConfigureAwait(false);
                }

                newPos = FractionalIndex.Between(beforeJob!.QueuePos, afterJob!.QueuePos);
            }
            else if (beforeJob is not null)
            {
                newPos = FractionalIndex.After(beforeJob.QueuePos);
            }
            else if (afterJob is not null)
            {
                newPos = FractionalIndex.Before(afterJob.QueuePos);
            }
            else
            {
                newPos = FractionalIndex.DefaultStep;
            }

            return await jobRepository.UpdateQueuePositionAsync(jobId, job.QueueId, newPos, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> MoveJobToQueueAsync(
        JobId jobId,
        int targetQueueId,
        JobId? beforeJobId = null,
        JobId? afterJobId = null,
        CancellationToken cancellationToken = default)
    {
        if (jobId == beforeJobId || jobId == afterJobId)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return false;
            }

            var targetQueue = await queueRepository.GetAsync(targetQueueId, cancellationToken).ConfigureAwait(false);
            if (targetQueue is null)
            {
                return false;
            }

            var beforeJob = beforeJobId is { } bId ? await jobRepository.GetAsync(bId, cancellationToken).ConfigureAwait(false) : null;
            var afterJob = afterJobId is { } aId ? await jobRepository.GetAsync(aId, cancellationToken).ConfigureAwait(false) : null;

            if (beforeJobId is not null && (beforeJob is null || beforeJob.QueueId != targetQueueId))
            {
                return false;
            }

            if (afterJobId is not null && (afterJob is null || afterJob.QueueId != targetQueueId))
            {
                return false;
            }

            double newPos;
            if (beforeJob is not null && afterJob is not null)
            {
                if (FractionalIndex.NeedsRebalance(beforeJob.QueuePos, afterJob.QueuePos))
                {
                    await RebalanceQueueUnderLockAsync(targetQueueId, cancellationToken).ConfigureAwait(false);
                    beforeJob = await jobRepository.GetAsync(beforeJob.Id, cancellationToken).ConfigureAwait(false);
                    afterJob = await jobRepository.GetAsync(afterJob.Id, cancellationToken).ConfigureAwait(false);
                }

                newPos = FractionalIndex.Between(beforeJob!.QueuePos, afterJob!.QueuePos);
            }
            else if (beforeJob is not null)
            {
                newPos = FractionalIndex.After(beforeJob.QueuePos);
            }
            else if (afterJob is not null)
            {
                newPos = FractionalIndex.Before(afterJob.QueuePos);
            }
            else
            {
                var targetJobs = await jobRepository.ListAsync(queueId: targetQueueId, cancellationToken: cancellationToken).ConfigureAwait(false);
                newPos = targetJobs.Count > 0 ? FractionalIndex.After(targetJobs.Max(j => j.QueuePos)) : FractionalIndex.DefaultStep;
            }

            int oldQueueId = job.QueueId;
            bool moved = await jobRepository.UpdateQueuePositionAsync(jobId, targetQueueId, newPos, cancellationToken).ConfigureAwait(false);
            if (moved)
            {
                if (_activeJobs.ContainsKey(jobId))
                {
                    _activeJobs[jobId] = targetQueueId;
                }

                if (IsQueueRunning(oldQueueId))
                {
                    await PumpQueueUnderLockAsync(oldQueueId, cancellationToken).ConfigureAwait(false);
                }

                if (IsQueueRunning(targetQueueId))
                {
                    await PumpQueueUnderLockAsync(targetQueueId, cancellationToken).ConfigureAwait(false);
                }
            }

            return moved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task PumpQueueUnderLockAsync(int queueId, CancellationToken cancellationToken)
    {
        if (!IsQueueRunning(queueId))
        {
            return;
        }

        var queue = await queueRepository.GetAsync(queueId, cancellationToken).ConfigureAwait(false);
        if (queue is null)
        {
            return;
        }

        var jobs = await jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var j in jobs)
        {
            if (IsActiveState(j.State))
            {
                _activeJobs.TryAdd(j.Id, queueId);
                _ranSinceStart[queueId] = true;
            }
        }

        int activeCount = _activeJobs.Values.Count(qid => qid == queueId);
        int availableSlots = queue.MaxParallel - activeCount;

        var queued = jobs.Where(j => j.State == JobState.Queued && !_activeJobs.ContainsKey(j.Id))
                         .OrderBy(j => j.QueuePos)
                         .ToList();

        if (availableSlots > 0)
        {
            foreach (var nextJob in queued)
            {
                if (availableSlots <= 0)
                {
                    break;
                }

                _activeJobs[nextJob.Id] = queueId;
                try
                {
                    await jobControl.StartJobAsync(nextJob.Id, cancellationToken).ConfigureAwait(false);
                    _ranSinceStart[queueId] = true;
                    availableSlots--;
                }
                catch
                {
                    _activeJobs.TryRemove(nextJob.Id, out _);
                    throw;
                }
            }
        }

        // A paused job is work the user has set aside, not work that is done; a queue that ran nothing has not completed.
        bool hasPendingWork = jobs.Any(j => j.State is JobState.Queued or JobState.Paused || IsActiveState(j.State) || _activeJobs.ContainsKey(j.Id));
        if (!hasPendingWork && _ranSinceStart.TryRemove(queueId, out _))
        {
            _runningQueues[queueId] = false;
            QueueCompleted?.Invoke(this, queueId);
        }
    }

    private async Task RebalanceQueueUnderLockAsync(int queueId, CancellationToken cancellationToken)
    {
        var jobs = await jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sorted = jobs.OrderBy(j => j.QueuePos).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            double newPos = (i + 1) * FractionalIndex.DefaultStep;
            if (Math.Abs(sorted[i].QueuePos - newPos) > 1e-9)
            {
                await jobRepository.UpdateQueuePositionAsync(sorted[i].Id, queueId, newPos, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsActiveState(JobState state) =>
        state is JobState.Probing
              or JobState.Downloading
              or JobState.Retrying
              or JobState.Muxing
              or JobState.Verifying;
}
