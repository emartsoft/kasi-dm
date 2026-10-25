using System.Collections.Concurrent;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Enforces per-queue concurrency limits (<see cref="Queue.MaxParallel"/>), dispatches queued jobs,
/// shifts failing jobs to queue tails, and manages fractional-index job reordering (TRD 10.1, KASI-QUE-001, KASI-QUE-002).
/// </summary>
public sealed class QueueRunner : IQueueRunner
{
    private readonly IQueueRepository _queueRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IJobControl _jobControl;
    private readonly IQueueQuota? _quota;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, bool> _runningQueues = new();
    private readonly ConcurrentDictionary<JobId, int> _activeJobs = new();
    private readonly ConcurrentDictionary<int, bool> _ranSinceStart = new();
    private readonly ConcurrentDictionary<int, bool> _quotaPaused = new();

    public QueueRunner(
        IQueueRepository queueRepository,
        IJobRepository jobRepository,
        IJobControl jobControl,
        IQueueQuota? quota = null)
    {
        _queueRepository = queueRepository;
        _jobRepository = jobRepository;
        _jobControl = jobControl;
        _quota = quota;
#pragma warning disable IDE0031 // an event add cannot use the null-conditional operator
        if (_quota is not null)
        {
            _quota.QuotaReached += (_, queueId) => _ = PauseForQuotaAsync(queueId);
        }
#pragma warning restore IDE0031
    }

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
            await _jobControl.PauseJobAsync(id, cancellationToken).ConfigureAwait(false);
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
                if (_quota is not null)
                {
                    await _quota.ReleaseAsync(jobId, cancellationToken).ConfigureAwait(false);
                }

                _activeJobs.TryRemove(jobId, out var queueId);
                if (queueId == 0)
                {
                    var job = await _jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
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
                    var allJobs = await _jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    double maxPos = allJobs.Count > 0 ? allJobs.Max(j => j.QueuePos) : 0.0;
                    double tailPos = FractionalIndex.After(maxPos);
                    await _jobRepository.UpdateQueuePositionAsync(jobId, queueId, tailPos, cancellationToken).ConfigureAwait(false);
                }

                if (queueId > 0 && IsQueueRunning(queueId))
                {
                    await PumpQueueUnderLockAsync(queueId, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (IsActiveState(newState))
            {
                var job = await _jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
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
            var job = await _jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return false;
            }

            var beforeJob = beforeJobId is { } bId ? await _jobRepository.GetAsync(bId, cancellationToken).ConfigureAwait(false) : null;
            var afterJob = afterJobId is { } aId ? await _jobRepository.GetAsync(aId, cancellationToken).ConfigureAwait(false) : null;

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
                    beforeJob = await _jobRepository.GetAsync(beforeJob.Id, cancellationToken).ConfigureAwait(false);
                    afterJob = await _jobRepository.GetAsync(afterJob.Id, cancellationToken).ConfigureAwait(false);
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

            return await _jobRepository.UpdateQueuePositionAsync(jobId, job.QueueId, newPos, cancellationToken).ConfigureAwait(false);
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
            var job = await _jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return false;
            }

            var targetQueue = await _queueRepository.GetAsync(targetQueueId, cancellationToken).ConfigureAwait(false);
            if (targetQueue is null)
            {
                return false;
            }

            var beforeJob = beforeJobId is { } bId ? await _jobRepository.GetAsync(bId, cancellationToken).ConfigureAwait(false) : null;
            var afterJob = afterJobId is { } aId ? await _jobRepository.GetAsync(aId, cancellationToken).ConfigureAwait(false) : null;

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
                    beforeJob = await _jobRepository.GetAsync(beforeJob.Id, cancellationToken).ConfigureAwait(false);
                    afterJob = await _jobRepository.GetAsync(afterJob.Id, cancellationToken).ConfigureAwait(false);
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
                var targetJobs = await _jobRepository.ListAsync(queueId: targetQueueId, cancellationToken: cancellationToken).ConfigureAwait(false);
                newPos = targetJobs.Count > 0 ? FractionalIndex.After(targetJobs.Max(j => j.QueuePos)) : FractionalIndex.DefaultStep;
            }

            int oldQueueId = job.QueueId;
            bool moved = await _jobRepository.UpdateQueuePositionAsync(jobId, targetQueueId, newPos, cancellationToken).ConfigureAwait(false);
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

        var queue = await _queueRepository.GetAsync(queueId, cancellationToken).ConfigureAwait(false);
        if (queue is null)
        {
            return;
        }

        var jobs = await _jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);

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

                if (_quota is not null && !await _quota.TryReserveAsync(nextJob, queue, cancellationToken).ConfigureAwait(false))
                {
                    _quotaPaused[queueId] = true;
                    _runningQueues[queueId] = false;
                    break;
                }

                _activeJobs[nextJob.Id] = queueId;
                try
                {
                    await _jobControl.StartJobAsync(nextJob.Id, cancellationToken).ConfigureAwait(false);
                    _ranSinceStart[queueId] = true;
                    availableSlots--;
                }
                catch
                {
                    _activeJobs.TryRemove(nextJob.Id, out _);
                    if (_quota is not null)
                    {
                        await _quota.ReleaseAsync(nextJob.Id, cancellationToken).ConfigureAwait(false);
                    }

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

    /// <summary>Restarts a queue that quota enforcement paused, once its period has capacity again (rollover or a cap change).</summary>
    public async Task<bool> TryResumeAfterQuotaAsync(int queueId, CancellationToken cancellationToken = default)
    {
        if (_quota is null)
        {
            return false;
        }

        var pausedForQuota = _quotaPaused.ContainsKey(queueId)
            || (await _jobRepository.ListAsync(queueId: queueId, state: JobState.Paused, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Any(job => job.PauseReason == PauseReason.Quota);
        if (!pausedForQuota || !await _quota.HasCapacityAsync(queueId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _quotaPaused.TryRemove(queueId, out _);
        foreach (var job in await _jobRepository.ListAsync(queueId: queueId, state: JobState.Paused, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (job.PauseReason == PauseReason.Quota)
            {
                await _jobRepository.TryChangeStateAsync(job.Id, JobState.Paused, JobState.Queued, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        await StartQueueAsync(queueId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task PauseForQuotaAsync(int queueId)
    {
        List<JobId> active;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _quotaPaused[queueId] = true;
            _runningQueues[queueId] = false;
            active = [.. _activeJobs.Where(kvp => kvp.Value == queueId).Select(kvp => kvp.Key)];
        }
        finally
        {
            _gate.Release();
        }

        foreach (var id in active)
        {
            try
            {
                await _jobControl.PauseJobAsync(id).ConfigureAwait(false);
                var job = await _jobRepository.GetAsync(id).ConfigureAwait(false);
                if (job is { State: JobState.Paused })
                {
                    await _jobRepository.TryChangeStateAsync(id, JobState.Paused, JobState.Paused, "Data quota reached", PauseReason.Quota).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A job that changed state meanwhile must not stop the others; the queue stays stopped either way.
            }
        }
    }

    private async Task RebalanceQueueUnderLockAsync(int queueId, CancellationToken cancellationToken)
    {
        var jobs = await _jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sorted = jobs.OrderBy(j => j.QueuePos).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            double newPos = (i + 1) * FractionalIndex.DefaultStep;
            if (Math.Abs(sorted[i].QueuePos - newPos) > 1e-9)
            {
                await _jobRepository.UpdateQueuePositionAsync(sorted[i].Id, queueId, newPos, cancellationToken).ConfigureAwait(false);
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
