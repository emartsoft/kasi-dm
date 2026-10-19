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
    private readonly ConcurrentDictionary<JobId, byte> _overwrite = new();
    private readonly SemaphoreSlim _addGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly Channel<EngineEvent> _events = Channel.CreateBounded<EngineEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
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
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _lifecycle.CancelAsync().ConfigureAwait(false);
        var running = _running.Values.ToArray();
        foreach (var job in running)
        {
            await job.Cancellation.CancelAsync().ConfigureAwait(false);
        }

#pragma warning disable VSTHRD003 // running jobs and the dispatcher are owned by this engine
        await Task.WhenAll(running.Select(j => j.Completion.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
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

            if (job.State != JobState.Queued)
            {
                throw new InvalidOperationException($"Job {id} is {job.State}, not Queued.");
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifecycle.Token, cancellationToken);
            var running = new RunningJob(cancellation);
            if (!_running.TryAdd(id, running))
            {
                throw new InvalidOperationException("The job is already running.");
            }

            try
            {
                await TransitionAsync(id, JobState.Queued, JobState.Probing, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                Publish(new JobProgressed(id, job.DoneBytes, job.TotalBytes, 0));
                await TransitionAsync(id, JobState.Probing, JobState.Downloading, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var outcome = await RunTransferAsync(job, running.Cancellation.Token).ConfigureAwait(false);
                await ApplyOutcomeAsync(job.Id, outcome).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await PauseFromCurrentAsync(id, PauseReason.UserRequested).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await FailFromCurrentAsync(id, $"The transfer could not be started ({ex.GetType().Name}).").ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(id, out _);
                running.Completion.TrySetResult();
            }
        }
        finally
        {
            gate.Release();
        }
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

        return new HttpTransferOptions
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
        };
    }

    private async Task ApplyOutcomeAsync(JobId id, TransferOutcome outcome)
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
                await PauseFromCurrentAsync(id, PauseReason.UserRequested).ConfigureAwait(false);
                break;

            case TransferFailed failed when failed.ErrorKind == TransferErrorKind.DiskNotEnoughSpace:
                await PauseFromCurrentAsync(id, PauseReason.NotEnoughSpace).ConfigureAwait(false);
                break;

            case TransferFailed failed:
                await FailFromCurrentAsync(id, failed.Detail).ConfigureAwait(false);
                break;
        }
    }

    private async Task TransitionAsync(
        JobId id,
        JobState from,
        JobState to,
        string? failureMessage = null,
        PauseReason? pauseReason = null,
        CancellationToken cancellationToken = default)
    {
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

    private Task PauseFromCurrentAsync(JobId id, PauseReason reason) =>
        TransitionFromCurrentAsync(id, JobState.Paused, reason, null);

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

        var ordered = segments.OrderBy(s => s.Index).ToArray();
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
