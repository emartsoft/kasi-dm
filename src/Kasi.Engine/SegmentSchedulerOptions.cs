namespace Kasi.Engine;

/// <summary>The tunables of <see cref="SegmentScheduler"/>, with the defaults of TRD 6.2 and 6.3 (ADR 0004).</summary>
public sealed record SegmentSchedulerOptions
{
    public const int DefaultMaxConnections = 8;
    public const int MinConnectionLimit = 1;
    public const int MaxConnectionLimit = 32;

    /// <summary>Most connections the download may use (the caller maps the job's or queue's setting here).</summary>
    public int MaxConnections { get; init; } = DefaultMaxConnections;

    /// <summary>A segment is only split when each half keeps at least this many unleased bytes.</summary>
    public long MinSplitSize { get; init; } = 1L * 1024 * 1024;

    /// <summary>Split points are rounded up to this offset multiple so writes stay page aligned.</summary>
    public long PageAlignment { get; init; } = 64L * 1024;

    public long MinLeaseBytes { get; init; } = 512L * 1024;

    public long MaxLeaseBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>A lease is sized to this much transfer time at the worker's current speed.</summary>
    public TimeSpan LeaseTargetDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A worker below this fraction of the median active speed counts as slow.</summary>
    public double SlowLaneSpeedRatio { get; init; } = 0.30;

    public TimeSpan SlowLaneSustainedDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A slow worker's segment must have more than this many times <see cref="MinSplitSize"/> left to be taken over.</summary>
    public long SlowLaneMinRemainingMultiplier { get; init; } = 4;

    /// <summary>The median is only meaningful with the slow worker plus at least two others active.</summary>
    public int SlowLaneMinActiveWorkers { get; init; } = 3;

    public long MaxEndgameDuplicateBytes { get; init; } = 2L * 1024 * 1024;

    public TimeSpan RampUpInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Aggregate throughput must rise by this fraction over the previous window for another connection to be added.</summary>
    public double RampUpThroughputGainThreshold { get; init; } = 0.05;

    /// <summary>Near the end of the file cap a lease at <c>remaining / connections</c> so one big lease does not leave the others idle. Off until measured (M1.15).</summary>
    public bool ShrinkTailLeases { get; init; }

    /// <summary>
    /// The size of the next lease for a worker moving <paramref name="bytesPerSecond"/>: <see cref="LeaseTargetDuration"/> of transfer,
    /// clamped to <see cref="MinLeaseBytes"/> .. <see cref="MaxLeaseBytes"/>. An unknown, zero or invalid speed gives the minimum.
    /// </summary>
    public long LeaseSizeFor(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return MinLeaseBytes;
        }

        var wanted = bytesPerSecond * LeaseTargetDuration.TotalSeconds;
        return wanted >= MaxLeaseBytes ? MaxLeaseBytes : Math.Max(MinLeaseBytes, (long)wanted);
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value the scheduler cannot work with.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConnections, MinConnectionLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConnections, MaxConnectionLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinSplitSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageAlignment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinLeaseBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLeaseBytes, MinLeaseBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseTargetDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SlowLaneSpeedRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(SlowLaneSpeedRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SlowLaneSustainedDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(SlowLaneMinRemainingMultiplier, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SlowLaneMinActiveWorkers, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxEndgameDuplicateBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RampUpInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(RampUpThroughputGainThreshold);
    }
}
