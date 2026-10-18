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

    public TimeSpan WriteBufferMaxAge { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan SupersededLeaseDrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Headers added to the probe and every lease request; tests use <c>X-Kasi-Chaos</c>.</summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }

    /// <summary>Retry classification and backoff for probes and leases (TRD 6.6).</summary>
    public TransferRetryPolicy RetryPolicy { get; init; } = new();

    /// <summary>The job whose segments are checkpointed; null disables checkpointing.</summary>
    public JobId? CheckpointJobId { get; init; }

    /// <summary>Writes flushed segment cursors; must be started by the caller when supplied.</summary>
    public ICheckpointWriter? CheckpointWriter { get; init; }

    /// <summary>Persists the segment structure (initial download and every split).</summary>
    public ISegmentRepository? SegmentRepository { get; init; }

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

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WriteBufferMaxAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SupersededLeaseDrainTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CheckpointInterval, TimeSpan.Zero);
        RetryPolicy.Validate();
        SchedulerOptions.Validate();
        if (CheckpointJobId is not null && (CheckpointWriter is null || SegmentRepository is null))
        {
            throw new ArgumentException("Checkpointing needs a job id, a checkpoint writer and a segment repository.");
        }
    }
}
