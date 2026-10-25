using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Event arguments raised when a periodic synchronization check run completes.
/// </summary>
public sealed class SyncRunCompletedEventArgs(int reDownloadedCount) : EventArgs
{
    public int ReDownloadedCount { get; } = reDownloadedCount;
}

/// <summary>
/// Coordinates periodic synchronization for jobs: re-checks remote URLs and re-downloads when
/// ETag, Last-Modified, or size changes (TRD 10.1, KASI-QUE-006, TC-QUE-008).
/// </summary>
public sealed class PeriodicSyncCoordinator : IDisposable
{
    private readonly IJobRepository _jobRepository;
    private readonly IJobControl _jobControl;
    private readonly IRemoteFileChecker _remoteChecker;
    private readonly ISegmentRepository? _segmentRepository;
    private readonly IQueueRunner? _runner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private int _disposed;

    public PeriodicSyncCoordinator(
        IJobRepository jobRepository,
        IJobControl jobControl,
        IRemoteFileChecker remoteChecker,
        ISegmentRepository? segmentRepository = null,
        IQueueRunner? runner = null,
        TimeProvider? timeProvider = null)
    {
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _jobControl = jobControl ?? throw new ArgumentNullException(nameof(jobControl));
        _remoteChecker = remoteChecker ?? throw new ArgumentNullException(nameof(remoteChecker));
        _segmentRepository = segmentRepository;
        _runner = runner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when a periodic check run finishes checking jobs.</summary>
    public event EventHandler<SyncRunCompletedEventArgs>? SyncRunCompleted;

    /// <summary>Raised when a periodic check run throws an exception.</summary>
    public event EventHandler<Exception>? SyncCheckFailed;

    /// <summary>
    /// Checks whether the remote resource for <paramref name="jobId"/> has changed. If changed,
    /// resets progress, transitions the job from Completed to Queued, and triggers a re-download.
    /// If unchanged, leaves the job in Completed and does not re-download.
    /// </summary>
    public async Task<bool> SyncJobAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.State != JobState.Completed || job.Kind != JobKind.Http)
        {
            return false;
        }

        bool changed;
        try
        {
            changed = await _remoteChecker.HasRemoteChangedAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (!changed)
        {
            return false;
        }

        _remoteChecker.PrepareForRedownload(job.Id);

        if (_segmentRepository is not null)
        {
            await _segmentRepository.ReplaceAsync(job.Id, [], cancellationToken).ConfigureAwait(false);
        }

        await _jobRepository.ResetProgressAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var transitioned = await _jobRepository.TryChangeStateAsync(
            job.Id,
            JobState.Completed,
            JobState.Queued,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!transitioned)
        {
            return false;
        }

        if (_runner is not null && _runner.IsQueueRunning(job.QueueId))
        {
            await _runner.PumpQueueAsync(job.QueueId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _jobControl.StartJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Checks all completed jobs in <paramref name="queueId"/> and triggers re-downloads for any that changed.
    /// Returns the number of re-downloads triggered.
    /// </summary>
    public async Task<int> SyncQueueAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var jobs = await _jobRepository.ListAsync(queueId: queueId, state: JobState.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
        int synced = 0;
        foreach (var job in jobs)
        {
            if (await SyncJobAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                synced++;
            }
        }

        return synced;
    }

    /// <summary>
    /// Checks all completed HTTP jobs across all queues and triggers re-downloads for any that changed.
    /// Returns the number of re-downloads triggered.
    /// </summary>
    public async Task<int> SyncAllCompletedAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _jobRepository.ListAsync(state: JobState.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
        int synced = 0;
        foreach (var job in jobs)
        {
            if (await SyncJobAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                synced++;
            }
        }

        return synced;
    }

    /// <summary>
    /// Starts timer-driven periodic synchronization checks at the specified interval.
    /// </summary>
    public void Start(TimeSpan interval)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _timer?.Dispose();
        _timer = _timeProvider.CreateTimer(
            callback: _ => _ = SyncAllSafeAsync(),
            state: null,
            dueTime: interval,
            period: interval);
    }

    /// <summary>Stops the periodic timer.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task SyncAllSafeAsync()
    {
        if (await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            try
            {
                int count = await SyncAllCompletedAsync().ConfigureAwait(false);
                SyncRunCompleted?.Invoke(this, new SyncRunCompletedEventArgs(count));
            }
            catch (Exception ex)
            {
                SyncCheckFailed?.Invoke(this, ex);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer?.Dispose();
        _gate.Dispose();
    }
}
