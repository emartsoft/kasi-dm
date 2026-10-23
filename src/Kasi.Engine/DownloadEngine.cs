using System.Collections.Concurrent;
using System.Threading.Channels;
using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// The headless engine facade (M1.13). It owns the job lifecycle, persists every transition before raising its event,
/// recovers interrupted jobs at start-up and runs one HTTP or FTP transfer at a time per job. Queues, UI and media are
/// outside this type.
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly IJobRepository _jobs;
    private readonly ISegmentRepository _segments;
    private readonly ICheckpointWriter _checkpoints;
    private readonly IJobSecretRepository? _secrets;
    private readonly BandwidthLimiter _bandwidth;
    private readonly ProxySettings _proxy;
    private readonly TimeProvider _time;
    private readonly INetworkStatus? _network;
    private readonly ConcurrentDictionary<JobId, SemaphoreSlim> _jobLocks = new();
    private readonly ConcurrentDictionary<JobId, RunningJob> _running = new();
    private readonly ConcurrentDictionary<JobId, PauseReason> _pauseIntents = new();
    private readonly ConcurrentDictionary<JobId, byte> _overwrite = new();
    private readonly SemaphoreSlim _addGate = new(1, 1);
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycle = new();
    // Unbounded on purpose: state events are rare and must never be dropped, and progress is throttled at its source.
    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Action<EngineEvent>> _subscribers = [];
    private Task? _dispatcher;
    private int _started;
    private int _stopped;
    private int _droppedEvents;
    private int _subscriberFaults;

    public DownloadEngine(
        IJobRepository jobs,
        ISegmentRepository segments,
        ICheckpointWriter checkpoints,
        BandwidthLimiter bandwidth,
        ProxySettings? proxy = null,
        IJobSecretRepository? secrets = null,
        TimeProvider? timeProvider = null,
        INetworkStatus? network = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(bandwidth);
        _jobs = jobs;
        _segments = segments;
        _checkpoints = checkpoints;
        _bandwidth = bandwidth;
        _proxy = proxy ?? new ProxySettings();
        _secrets = secrets;
        _time = timeProvider ?? TimeProvider.System;
        _network = network;
    }

    /// <summary>Raised on the single dispatcher after the transition is committed. A throwing handler is counted, never fatal.</summary>
    public event Action<EngineEvent>? EventRaised
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_subscribers)
            {
                _subscribers.Add(value);
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_subscribers)
            {
                _subscribers.Remove(value);
            }
        }
    }

    public int DroppedEventCount => Volatile.Read(ref _droppedEvents);

    public int SubscriberFaultCount => Volatile.Read(ref _subscriberFaults);

    /// <summary>Test-only hook for request headers, a fake disk-space provider or fault injection.</summary>
    internal Func<Job, HttpTransferOptions, HttpTransferOptions>? OptionsDecorator { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _dispatcher = Task.Run(DispatchAsync, CancellationToken.None);
        if (!await _jobs.QuickCheckAsync(cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _started, 0);
            _events.Writer.TryComplete();
            throw new InvalidOperationException("The job database failed its integrity check; no job was started.");
        }

        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (_network is not null)
        {
            _network.OnlineChanged += OnNetworkChanged;
            await ResumeNetworkPausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _lifecycle.CancelAsync().ConfigureAwait(false);
        _network?.OnlineChanged -= OnNetworkChanged;

        var running = _running.ToArray();
        foreach (var (id, job) in running)
        {
            _pauseIntents[id] = PauseReason.UserRequested;
            await job.Cancellation.CancelAsync().ConfigureAwait(false);
        }

#pragma warning disable VSTHRD003 // running jobs and the dispatcher are owned by this engine
        await Task.WhenAll(running.Select(pair => pair.Value.Completion.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
        _events.Writer.TryComplete();
        if (_dispatcher is not null)
        {
            await _dispatcher.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _addGate.Dispose();
        _events.Writer.TryComplete();
    }

    public Task<DuplicateCheckResult> CheckDuplicateAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return DuplicateDetector.CheckAsync(_jobs, new Uri(job.Url, UriKind.Absolute), job.Folder, job.FileName, cancellationToken);
    }

    /// <summary>
    /// Adds a job after the duplicate check. With no resolution a conflict is returned and nothing is added; AutoRename
    /// applies the suggested name. Resume is returned to the caller, who starts the existing job.
    /// </summary>
    public async Task<DuplicateCheckResult> AddJobAsync(
        Job job,
        DuplicateAction? resolution = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(job);
        if (job.State != JobState.Queued || job.DoneBytes != 0 || job.Kind is not (JobKind.Http or JobKind.Ftp))
        {
            throw new ArgumentException("A new HTTP or FTP job must be Queued with no progress.", nameof(job));
        }

        await _addGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var check = await DuplicateDetector.CheckAsync(
                _jobs, new Uri(job.Url, UriKind.Absolute), job.Folder, job.FileName, cancellationToken).ConfigureAwait(false);
            if (check.HasConflict && resolution is null or DuplicateAction.Resume)
            {
                return check;
            }

            var added = check.HasConflict && resolution == DuplicateAction.AutoRename
                ? job with { FileName = check.SuggestedFileName }
                : job;
            if (resolution == DuplicateAction.Overwrite)
            {
                _overwrite[job.Id] = 0;
            }

            await _jobs.AddAsync(added, cancellationToken).ConfigureAwait(false);
            Publish(new JobAdded(added));
            return check with { ExistingJob = null };
        }
        finally
        {
            _addGate.Release();
        }
    }

    public async Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var gate = _jobLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await GetStartableJobAsync(id, JobState.Queued, cancellationToken).ConfigureAwait(false);
            var running = BeginRunning(id);
            LaunchRunning(id, running, () => RunQueuedAsync(job, running.Cancellation.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Cancels a running job, waits for its final checkpoint and stores the user pause reason.</summary>
    public async Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (_running.TryGetValue(id, out var running))
        {
            _pauseIntents[id] = PauseReason.UserRequested;
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // the running job is owned by this engine
            await running.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            return;
        }

        var job = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {id} does not exist.");
        if (job.State == JobState.Paused)
        {
            return;
        }

        if (job.State is JobState.Completed or JobState.Failed)
        {
            throw new InvalidOperationException($"A {job.State} job cannot be paused.");
        }

        await TransitionAsync(id, job.State, JobState.Paused, pauseReason: PauseReason.UserRequested, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var gate = _jobLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await GetStartableJobAsync(id, JobState.Paused, cancellationToken).ConfigureAwait(false);
            var segments = await _segments.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var running = BeginRunning(id);
            LaunchRunning(id, running, async () =>
            {
                if (NeedsRestart(job, segments))
                {
                    await _segments.ReplaceAsync(id, [], CancellationToken.None).ConfigureAwait(false);
                    await _jobs.ResetProgressAsync(id, CancellationToken.None).ConfigureAwait(false);
                    await TransitionAsync(id, JobState.Paused, JobState.Queued, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    await RunQueuedAsync(job with { State = JobState.Queued }, running.Cancellation.Token).ConfigureAwait(false);
                    return;
                }

                await TransitionAsync(id, JobState.Paused, JobState.Downloading, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var outcome = await RunResumeAsync(job, segments, running.Cancellation.Token).ConfigureAwait(false);
                await ApplyOutcomeAsync(job.Id, outcome, running.Cancellation.Token).ConfigureAwait(false);
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops the runner and removes the job; the files are deleted only when explicitly requested.</summary>
    public async Task CancelJobAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (_running.TryGetValue(id, out var running))
        {
            _pauseIntents[id] = PauseReason.UserRequested;
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // the running job is owned by this engine
            await running.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        var job = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not null)
        {
            if (deleteFiles)
            {
                DeleteTransferFiles(job);
            }

            await _jobs.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }

        _overwrite.TryRemove(id, out _);
        Publish(new JobRemoved(id));
    }

    /// <summary>Validates a refreshed signed link for a paused HTTP job and continues from its saved cursors.</summary>
    public async Task<TransferOutcome> RefreshLinkAsync(
        JobId id,
        Uri refreshedUrl,
        JobSecret? newSecret = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(refreshedUrl);
        var gate = _jobLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {id} does not exist.");
            if (job.Kind != JobKind.Http)
            {
                throw new NotSupportedException("Link refresh is currently supported for HTTP jobs.");
            }

            if (job.State != JobState.Paused)
            {
                throw new InvalidOperationException("Only a paused job can refresh its link.");
            }

            if (job.TotalBytes is not { } total || job.ETag is null && job.LastModified is null)
            {
                throw new InvalidOperationException("The job has no resumable checkpoint to validate the refreshed link against.");
            }

            var segments = await _segments.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("The job has no saved segments to resume after refreshing its link.");
            }

            var finalUrl = new Uri(job.FinalUrl ?? job.Url, UriKind.Absolute);
            var options = BuildOptions(job, finalUrl) with { Secret = newSecret };
            var running = BeginRunning(id);
            TransferOutcome? outcome = null;
            await ExecuteRunningAsync(id, running, async () =>
            {
                await TransitionAsync(id, JobState.Paused, JobState.Downloading, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
                    finalUrl,
                    refreshedUrl,
                    job.Folder,
                    job.FileName,
                    total,
                    job.ETag,
                    job.LastModified,
                    segments,
                    options,
                    running.Cancellation.Token).ConfigureAwait(false);

                if (outcome is TransferFailed { ErrorKind: TransferErrorKind.FileChanged } rejected)
                {
                    // the refreshed link serves another file: stay paused with the reason, never restart on the user's behalf
                    await PauseFromCurrentAsync(id, job.PauseReason ?? PauseReason.AuthRequired, rejected.Detail).ConfigureAwait(false);
                }
                else
                {
                    await ApplyOutcomeAsync(id, outcome, running.Cancellation.Token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            return outcome ?? new TransferFailed { ErrorKind = TransferErrorKind.Cancelled, Detail = "The link refresh did not run." };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Job> GetStartableJobAsync(JobId id, JobState expected, CancellationToken cancellationToken)
    {
        if (_running.ContainsKey(id))
        {
            throw new InvalidOperationException("The job is already running.");
        }

        var job = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {id} does not exist.");
        if (job.Kind is not (JobKind.Http or JobKind.Ftp))
        {
            throw new NotSupportedException($"Jobs of kind '{job.Kind}' are not handled by the engine facade yet.");
        }

        if (job.State != expected)
        {
            throw new InvalidOperationException($"Job {id} is {job.State}, not {expected}.");
        }

        return job;
    }

    /// <summary>Completes when the job is no longer running (finished, paused, failed or stopped); returns at once if it is not running.</summary>
    public async Task WaitForJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(id, out var running))
        {
#pragma warning disable VSTHRD003 // the running job is owned by this engine
            await running.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
    }

    // The run belongs to the engine, not to the caller of Start/Resume: only the engine's lifecycle and the pause/cancel
    // requests stop it, so a caller's short-lived token must not pause the job after the call returned.
    private RunningJob BeginRunning(JobId id)
    {
#pragma warning disable CA2000 // RunningJob owns the linked source until ExecuteRunningAsync disposes it
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifecycle.Token);
#pragma warning restore CA2000
        var running = new RunningJob(linked);
        if (!_running.TryAdd(id, running))
        {
            linked.Dispose();
            throw new InvalidOperationException("The job is already running.");
        }

        return running;
    }

    private void LaunchRunning(JobId id, RunningJob running, Func<Task> action) =>
        _ = Task.Run(() => ExecuteRunningAsync(id, running, action), CancellationToken.None);

    private async Task ExecuteRunningAsync(JobId id, RunningJob running, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PauseFromCurrentAsync(id, TakePauseReason(id), message: null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await FailFromCurrentAsync(id, $"The transfer could not be started ({ex.GetType().Name}).").ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(id, out _);
            running.Completion.TrySetResult();
            running.Cancellation.Dispose();
        }
    }

    private async Task RunQueuedAsync(Job job, CancellationToken cancellationToken)
    {
        await TransitionAsync(job.Id, JobState.Queued, JobState.Probing, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Publish(new JobProgressed(job.Id, job.DoneBytes, job.TotalBytes, 0));
        await TransitionAsync(job.Id, JobState.Probing, JobState.Downloading, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var outcome = await RunTransferAsync(job, cancellationToken).ConfigureAwait(false);
        await ApplyOutcomeAsync(job.Id, outcome, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransferOutcome> RunTransferAsync(Job job, CancellationToken cancellationToken)
    {
        var url = new Uri(job.Url, UriKind.Absolute);
        var settings = BuildOptions(job, url);
        return job.Kind switch
        {
            JobKind.Http => await new HttpTransferRunner().DownloadAsync(url, job.Folder, settings, cancellationToken).ConfigureAwait(false),
            JobKind.Ftp => await new FtpTransferRunner().DownloadAsync(url, job.Folder, settings, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Jobs of kind '{job.Kind}' are not handled by the engine facade yet."),
        };
    }

    private async Task<TransferOutcome> RunResumeAsync(Job job, IReadOnlyList<Segment> segments, CancellationToken cancellationToken)
    {
        var url = new Uri(job.FinalUrl ?? job.Url, UriKind.Absolute);
        var settings = BuildOptions(job, url);
        var total = job.TotalBytes ?? throw new InvalidOperationException("A resumable job needs its total size.");
        return job.Kind switch
        {
            JobKind.Http => await new HttpTransferRunner().ResumeAsync(
                url,
                job.Folder,
                job.FileName,
                total,
                job.ETag,
                job.LastModified,
                segments,
                settings,
                cancellationToken).ConfigureAwait(false),
            JobKind.Ftp => await new FtpTransferRunner().ResumeAsync(
                url,
                job.Folder,
                job.FileName,
                total,
                job.LastModified,
                segments,
                settings,
                cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Jobs of kind '{job.Kind}' are not handled by the engine facade yet."),
        };
    }

    private static bool NeedsRestart(Job job, IReadOnlyList<Segment> segments) =>
        !job.Resumable
        || job.TotalBytes is null
        || segments.Count == 0
        || (job.ETag is null && job.LastModified is null);

    private HttpTransferOptions BuildOptions(Job job, Uri url)
    {
        var connections = job.Connections ?? 4;
        ExpectedChecksum? expected = null;
        if (job.ChecksumAlgo is not null || job.Checksum is not null)
        {
            if (!ExpectedChecksum.TryParse(job.ChecksumAlgo, job.Checksum, out expected))
            {
                throw new ArgumentException("The job has an invalid published checksum.");
            }
        }

        var options = new HttpTransferOptions
        {
            SchedulerOptions = new SegmentSchedulerOptions
            {
                MaxConnections = connections,
                InitialConnections = connections,
            },
            OverrideFileName = job.FileName,
            OverwriteDestination = _overwrite.ContainsKey(job.Id),
            CheckpointJobId = job.Id,
            CheckpointWriter = _checkpoints,
            SegmentRepository = _segments,
            JobRepository = _jobs,
            BandwidthLimiter = _bandwidth,
            JobBandwidthLimit = _bandwidth.CreateJobLimit(job.SpeedLimitBps),
            Proxy = _proxy,
            TimeProvider = _time,
            SecretRepository = _secrets,
            InitialUrl = url.Scheme is "http" or "https" ? url : null,
            ExpectedChecksum = expected,
            SchedulerTick = ProgressPublisher(job),
            Log = line => Publish(new JobLogged(job.Id, _time.GetUtcNow(), line)),
            BeforeVerifyAsync = () => EnterVerifyingAsync(job.Id),
        };
        return OptionsDecorator is { } decorator ? decorator(job, options) : options;
    }

    // At most one speed sample per second per job, with the speed measured over that second, and the shape of the transfer (segments and
    // connections) about four times a second for the details window. Nothing here runs per chunk: it runs on the scheduler's pulse.
    private static readonly TimeSpan _segmentInterval = TimeSpan.FromMilliseconds(250);

    private Action<SegmentScheduler> ProgressPublisher(Job job)
    {
        var last = _time.GetTimestamp();
        long lastBytes = 0;
        var seeded = false;
        var lastShape = _time.GetTimestamp() - _time.TimestampFrequency; // so the first pulse publishes at once
        var seenSegments = 0;
        var seenConnections = -1;
        var leaseMarks = new Dictionary<int, (long Start, long Cursor, long Stamp)>();
        return scheduler =>
        {
            PublishShape(scheduler);
            if (!seeded)
            {
                // A resumed or recovered job starts with its saved bytes already counted; they were not transferred in the
                // first second, so the baseline is taken from the scheduler instead of zero.
                lastBytes = scheduler.TotalWritten;
                last = _time.GetTimestamp();
                seeded = true;
                return;
            }

            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(last, now);
            if (elapsed < TimeSpan.FromSeconds(1))
            {
                return;
            }

            var written = scheduler.TotalWritten;
            Publish(new JobProgressed(job.Id, written, scheduler.TotalBytes, Math.Max(0, (long)((written - lastBytes) / elapsed.TotalSeconds))));
            last = now;
            lastBytes = written;
        };

        void PublishShape(SegmentScheduler scheduler)
        {
            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(lastShape, now) < _segmentInterval)
            {
                return;
            }

            var segments = scheduler.GetSegmentsSnapshot();
            var leases = scheduler.GetLeaseSnapshot();
            var connections = new List<ConnectionView>(leases.Count);
            foreach (var lease in leases)
            {
                var speed = 0L;
                if (leaseMarks.TryGetValue(lease.Worker, out var mark) && mark.Start == lease.Start && lease.Cursor >= mark.Cursor)
                {
                    var seconds = _time.GetElapsedTime(mark.Stamp, now).TotalSeconds;
                    speed = seconds > 0 ? (long)((lease.Cursor - mark.Cursor) / seconds) : 0;
                }

                leaseMarks[lease.Worker] = (lease.Start, lease.Cursor, now);
                connections.Add(new ConnectionView(lease.Worker, lease.SegmentIndex, lease.Start, lease.End, speed));
            }

            lastShape = now;
            if (seenSegments > 0 && segments.Count > seenSegments)
            {
                Publish(new JobLogged(job.Id, _time.GetUtcNow(), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Slow lane: split, {segments.Count} segments")));
            }

            if (connections.Count != seenConnections)
            {
                Publish(new JobLogged(job.Id, _time.GetUtcNow(), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Connections: {connections.Count}")));
            }

            seenSegments = segments.Count;
            seenConnections = connections.Count;
            Publish(new SegmentsProgressed(job.Id, segments, connections));
        }
    }

    private async Task EnterVerifyingAsync(JobId id)
    {
        try
        {
            await TransitionAsync(id, JobState.Downloading, JobState.Verifying, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // the job was paused or removed at this instant; the outcome handling copes with whatever state it finds
        }
    }

    private async Task ApplyOutcomeAsync(JobId id, TransferOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case TransferCompleted completed:
                await TransitionFromCurrentAsync(id, JobState.Verifying).ConfigureAwait(false);
                await TransitionFromCurrentAsync(id, JobState.Completed).ConfigureAwait(false);
                Publish(new JobProgressed(id, completed.TotalBytes, completed.TotalBytes, 0));
                _overwrite.TryRemove(id, out _);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.Cancelled:
                await PauseFromCurrentAsync(id, TakePauseReason(id), message: null).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.DiskNotEnoughSpace:
                await PauseFromCurrentAsync(id, PauseReason.NotEnoughSpace, message: null).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.CheckpointError:
                await PauseFromCurrentAsync(id, PauseReason.CheckpointFault, message: failed.Detail).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.Offline:
                await PauseFromCurrentAsync(id, PauseReason.NetworkOffline, message: failed.Detail).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.FileChanged:
                await RestartAfterChangeAsync(id, failed, cancellationToken).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.ProxyAuthenticationRequired
                || failed.ProbeFailure?.Kind == ProbeErrorKind.AuthRequired:
                await PauseFromCurrentAsync(id, PauseReason.AuthRequired, message: failed.Detail).ConfigureAwait(false);
                break;

            case TransferFailed failed:
                await FailFromCurrentAsync(id, failed.Detail).ConfigureAwait(false);
                break;
        }
    }

    private async Task RestartAfterChangeAsync(JobId id, TransferFailed failure, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        if (job.RestartCount >= 2)
        {
            await FailFromCurrentAsync(id, "File changed repeatedly.").ConfigureAwait(false);
            return;
        }

        var discarded = failure.BytesFlushed > 0
            ? failure.BytesFlushed
            : failure.SegmentsSnapshot.Sum(segment => segment.Written);
        TryDelete(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension));
        await _segments.ReplaceAsync(id, [], CancellationToken.None).ConfigureAwait(false);
        await _jobs.ResetProgressAsync(id, CancellationToken.None).ConfigureAwait(false);
        var count = await _jobs.IncrementRestartCountAsync(id, CancellationToken.None).ConfigureAwait(false) ?? job.RestartCount + 1;
        Publish(new JobRestarted(id, "The remote file changed.", discarded));
        Publish(new JobProgressed(id, 0, job.TotalBytes, 0));

        var refreshed = job with
        {
            DoneBytes = 0,
            RestartCount = count,
            ETag = null,
            LastModified = null,
            TotalBytes = null,
        };
        var outcome = await RunTransferAsync(refreshed, cancellationToken).ConfigureAwait(false);
        await ApplyOutcomeAsync(id, outcome, cancellationToken).ConfigureAwait(false);
    }

    internal async Task TransitionAsync(
        JobId id,
        JobState from,
        JobState to,
        string? failureMessage = null,
        PauseReason? pauseReason = null,
        CancellationToken cancellationToken = default)
    {
        // the engine handles HTTP and FTP jobs only, and the kind matters only for the media muxing edge
        if (!JobStateMachine.CanTransition(JobKind.Http, from, to) && !JobStateMachine.CanRecover(from, to))
        {
            throw new InvalidOperationException($"Job {id}: {from} to {to} is not a legal transition.");
        }

        var changed = await _jobs.TryChangeStateAsync(id, from, to, failureMessage, pauseReason, cancellationToken).ConfigureAwait(false);
        if (!changed)
        {
            var stored = await _jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (stored?.State == to)
            {
                return;
            }

            throw new InvalidOperationException($"Job {id} could not move from {from} to {to}.");
        }

        Publish(new JobStateChanged(id, from, to, failureMessage));
    }

    private async Task TransitionFromCurrentAsync(JobId id, JobState to)
    {
        var job = await _jobs.GetAsync(id).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        if (job.State == to)
        {
            return;
        }

        await TransitionAsync(id, job.State, to).ConfigureAwait(false);
    }

    private Task PauseFromCurrentAsync(JobId id, PauseReason reason, string? message) =>
        TransitionFromCurrentAsync(id, JobState.Paused, reason, message);

    private PauseReason TakePauseReason(JobId id) =>
        _pauseIntents.TryRemove(id, out var reason) ? reason : PauseReason.UserRequested;

    private async Task StorePausedErrorAsync(JobId id, string message)
    {
        var job = await _jobs.GetAsync(id).ConfigureAwait(false);
        if (job?.State == JobState.Paused)
        {
            await _jobs.TryChangeStateAsync(id, JobState.Paused, JobState.Paused, message, job.PauseReason ?? PauseReason.UserRequested).ConfigureAwait(false);
        }
    }

    private static void DeleteTransferFiles(Job job)
    {
        var finalPath = Path.Combine(job.Folder, job.FileName);
        TryDelete(finalPath);
        TryDelete(finalPath + PartFileWriter.PartExtension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that is still locked is left for the caller to retry; the job row is already gone.
        }
    }

    private async Task TransitionFromCurrentAsync(JobId id, JobState to, PauseReason reason, string? message)
    {
        var job = await _jobs.GetAsync(id).ConfigureAwait(false);
        if (job is null || job.State == to)
        {
            return;
        }

        await TransitionAsync(id, job.State, to, message, reason).ConfigureAwait(false);
    }

    private async Task FailFromCurrentAsync(JobId id, string message)
    {
        var job = await _jobs.GetAsync(id).ConfigureAwait(false);
        if (job is null || job.State is JobState.Failed or JobState.Completed)
        {
            return;
        }

        await TransitionAsync(id, job.State, JobState.Failed, message).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var active = new List<Job>();
        foreach (var state in new[] { JobState.Probing, JobState.Downloading, JobState.Retrying, JobState.Verifying })
        {
            active.AddRange(await _jobs.ListAsync(state: state, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        foreach (var job in active)
        {
            await RecoverJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecoverJobAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.State == JobState.Probing)
        {
            await TransitionAsync(job.Id, JobState.Probing, JobState.Queued, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var finalPath = Path.Combine(job.Folder, job.FileName);
        var partPath = finalPath + PartFileWriter.PartExtension;
        if (File.Exists(finalPath) && !File.Exists(partPath) && await FinalFileIsCompleteAsync(job, finalPath, cancellationToken).ConfigureAwait(false))
        {
            await TransitionAsync(job.Id, job.State, JobState.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
            Publish(new JobProgressed(job.Id, job.TotalBytes ?? new FileInfo(finalPath).Length, job.TotalBytes, 0));
            return;
        }

        if (File.Exists(finalPath)
            && !File.Exists(partPath)
            && job.TotalBytes is { } expectedLength
            && new FileInfo(finalPath).Length == expectedLength
            && (job.ChecksumAlgo is not null || job.Checksum is not null))
        {
            await TransitionAsync(job.Id, job.State, JobState.Failed, "Verification failed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var segments = await _segments.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (File.Exists(partPath) && segments.Count > 0 && PartMatches(job, partPath, segments))
        {
            if (job.State == JobState.Verifying && segments.All(s => s.IsComplete))
            {
                var outcome = await RunResumeAsync(job, segments, cancellationToken).ConfigureAwait(false);
                if (outcome is TransferCompleted)
                {
                    await TransitionAsync(job.Id, JobState.Verifying, JobState.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
                    Publish(new JobProgressed(job.Id, job.TotalBytes ?? segments.Sum(s => s.Written), job.TotalBytes, 0));
                }
                else
                {
                    var message = outcome is TransferFailed failed ? failed.Detail : "Verification failed.";
                    await TransitionAsync(job.Id, JobState.Verifying, JobState.Failed, message, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await _jobs.ReconcileProgressAsync(job.Id, segments.Sum(s => s.Written), cancellationToken).ConfigureAwait(false);
            await TransitionAsync(
                job.Id,
                job.State,
                JobState.Paused,
                pauseReason: PauseReason.CrashRecovery,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        await _segments.ReplaceAsync(job.Id, [], cancellationToken).ConfigureAwait(false);
        await _jobs.ResetProgressAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (job.State == JobState.Verifying)
        {
            await TransitionAsync(job.Id, job.State, JobState.Paused, pauseReason: PauseReason.CrashRecovery, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TransitionAsync(job.Id, job.State, JobState.Paused, pauseReason: PauseReason.CrashRecovery, cancellationToken: cancellationToken).ConfigureAwait(false);
            await TransitionAsync(job.Id, JobState.Paused, JobState.Queued, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> FinalFileIsCompleteAsync(Job job, string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (job.TotalBytes is { } total && length != total)
        {
            return false;
        }

        if (job.ChecksumAlgo is null && job.Checksum is null)
        {
            return true;
        }

        if (!ExpectedChecksum.TryParse(job.ChecksumAlgo, job.Checksum, out var expected))
        {
            return false;
        }

        var actual = await ChecksumVerifier.ComputeAsync(path, expected!.Algorithm, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expected.Hex, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PartMatches(Job job, string partPath, IReadOnlyList<Segment> segments)
    {
        if (!job.Resumable || job.TotalBytes is not { } total || new FileInfo(partPath).Length != total)
        {
            return false;
        }

        var ordered = segments.OrderBy(s => s.Start).ToArray();
        if (ordered.Length == 0 || ordered[0].Start != 0 || ordered[^1].End != total)
        {
            return false;
        }

        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Start != ordered[i - 1].End)
            {
                return false;
            }
        }

        return ordered.All(s => s.Written >= 0 && s.Written <= s.Length);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _ = HandleNetworkChangedAsync();

    private async Task HandleNetworkChangedAsync()
    {
        if (Volatile.Read(ref _stopped) != 0 || _network is null)
        {
            return;
        }

        // Changes are handled one after another, never dropped: a reconnect that arrives while the offline pause is still
        // running waits for it and is then acted on.
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopped) != 0 || _network is null)
            {
                return;
            }

            if (!_network.IsOnline)
            {
                await PauseAllForOfflineAsync().ConfigureAwait(false);
            }
            else
            {
                await ResumeNetworkPausedAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private async Task PauseAllForOfflineAsync()
    {
        var running = _running.ToArray();
        foreach (var (id, job) in running)
        {
            _pauseIntents[id] = PauseReason.NetworkOffline;
            await job.Cancellation.CancelAsync().ConfigureAwait(false);
        }

#pragma warning disable VSTHRD003 // the running jobs are owned by this engine
        await Task.WhenAll(running.Select(pair => pair.Value.Completion.Task)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    private async Task ResumeNetworkPausedAsync(CancellationToken cancellationToken)
    {
        if (_network is { IsOnline: false })
        {
            return;
        }

        var paused = await _jobs.ListAsync(state: JobState.Paused, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var job in paused.Where(j => j.PauseReason == PauseReason.NetworkOffline))
        {
            try
            {
                await ResumeJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await StorePausedErrorAsync(job.Id, $"The job could not resume after reconnecting ({ex.GetType().Name}).").ConfigureAwait(false);
            }
        }
    }

    private void Publish(EngineEvent engineEvent)
    {
        if (!_events.Writer.TryWrite(engineEvent))
        {
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var engineEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Action<EngineEvent>[] handlers;
                lock (_subscribers)
                {
                    handlers = [.. _subscribers];
                }

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(engineEvent);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Interlocked.Increment(ref _subscriberFaults);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("StartAsync must complete before the engine accepts jobs.");
        }

        // after StopAsync the lifecycle token source may be disposed; a late call must fail cleanly instead of racing it
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
    }

    private sealed class RunningJob(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
