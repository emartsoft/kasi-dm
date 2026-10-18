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
        SchedulerOptions.Validate();
    }
}
