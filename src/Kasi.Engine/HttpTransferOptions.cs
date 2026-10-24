using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Options for one <see cref="HttpTransferRunner"/> download or resume (M1.7b).</summary>
public sealed record HttpTransferOptions
{
    public SegmentSchedulerOptions SchedulerOptions { get; init; } = new();

    /// <summary>Replaces the filename the probe resolved; the sanitiser still applies.</summary>
    public string? OverrideFileName { get; init; }

    /// <summary>When false (the default), an existing final file fails with <see cref="TransferErrorKind.DiskTargetExists"/>.</summary>
    public bool OverwriteDestination { get; init; }

    public IDiskSpaceProvider? SpaceProvider { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Optional published checksum, verified after sealing and before committing the final file.</summary>
    public ExpectedChecksum? ExpectedChecksum { get; init; }

    /// <summary>Per-transfer proxy routing; defaults to the operating system proxy.</summary>
    public ProxySettings Proxy { get; init; } = new();

    /// <summary>Shared global byte budget; pass the same instance to concurrent jobs and future torrent traffic.</summary>
    public BandwidthLimiter? BandwidthLimiter { get; init; }

    /// <summary>Optional job byte budget created by <see cref="BandwidthLimiter.CreateJobLimit"/>.</summary>
    public BandwidthLimiter.JobBandwidthLimit? JobBandwidthLimit { get; init; }

    public TimeSpan WriteBufferMaxAge { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan SupersededLeaseDrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Caller headers for probes and leases; sensitive names stay on the initial origin. Tests use <c>X-Kasi-Chaos</c>.</summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }

    /// <summary>The original job URL when resuming without a job repository; needed to scope credentials and sensitive headers.</summary>
    public Uri? InitialUrl { get; init; }

    /// <summary>Per-job cookies, captured headers and HTTP credentials. When null, a saved secret may be loaded.</summary>
    public JobSecret? Secret { get; init; }

    /// <summary>Loads the job's secret on resume and removes it after a completed transfer.</summary>
    public IJobSecretRepository? SecretRepository { get; init; }

    /// <summary>Retry classification and backoff for probes and leases (TRD 6.6).</summary>
    public TransferRetryPolicy RetryPolicy { get; init; } = new();

    /// <summary>The job whose segments are checkpointed; null disables checkpointing.</summary>
    public JobId? CheckpointJobId { get; init; }

    /// <summary>Writes flushed segment cursors; must be started by the caller when supplied.</summary>
    public ICheckpointWriter? CheckpointWriter { get; init; }

    /// <summary>Persists the segment structure (initial download and every split).</summary>
    public ISegmentRepository? SegmentRepository { get; init; }

    /// <summary>
    /// Asked once after the probe, before the part file exists, with the file's name and the MIME type the server reported: returns the folder to
    /// save in instead of the one the transfer was started with, or null to keep it (KASI-CAT-001). A failure here never fails the download.
    /// </summary>
    public Func<string, string?, CancellationToken, Task<string?>>? RouteAfterProbe { get; init; }

    /// <summary>Persists the probe's URL, filename, size and validators so a restart can resume (M1.9).</summary>
    public IJobRepository? JobRepository { get; init; }

    /// <summary>
    /// How often the active transfer flushes the part file and records its cursors (TRD 6.5). Data lost on a kill is at most this
    /// interval plus <see cref="WriteBufferMaxAge"/> (unwritten bytes are not in the snapshot), so 1.5 s plus 0.5 s keeps
    /// KASI-NFR-007's two seconds.
    /// </summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Test-only mid-transfer write fault; the runner forwards it to the part writer.</summary>
    internal Func<long, int, Exception?>? WriteFaultInjector { get; init; }

    /// <summary>Test-only flush fault; the runner forwards it to the part writer.</summary>
    internal Func<Exception?>? FlushFaultInjector { get; init; }

    /// <summary>Test-only observation hook, called on each scheduler pulse; production leaves it null.</summary>
    internal Action<SegmentScheduler>? SchedulerTick { get; init; }

    /// <summary>Called with a short line (numbers and fixed words only) when the runner decides something worth showing: the probe result, a retry.</summary>
    internal Action<string>? Log { get; init; }

    internal RequestScope? Scope { get; init; }

    /// <summary>Called after the part file is sealed and before the published checksum is computed; the facade shows Verifying.</summary>
    internal Func<Task>? BeforeVerifyAsync { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WriteBufferMaxAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SupersededLeaseDrainTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CheckpointInterval, TimeSpan.Zero);
        RetryPolicy.Validate();
        SchedulerOptions.Validate();
        ExpectedChecksum?.Validate();
        Proxy.Validate();
        if (JobBandwidthLimit is not null && (BandwidthLimiter is null || !BandwidthLimiter.Owns(JobBandwidthLimit)))
        {
            throw new ArgumentException("The job bandwidth budget must belong to the supplied bandwidth limiter.");
        }
        if (InitialUrl is not null && (!InitialUrl.IsAbsoluteUri || InitialUrl.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The initial URL must be an absolute HTTP or HTTPS URL.");
        }
        if (CheckpointJobId is not null && (CheckpointWriter is null || SegmentRepository is null))
        {
            throw new ArgumentException("Checkpointing needs a job id, a checkpoint writer and a segment repository.");
        }
    }
}
