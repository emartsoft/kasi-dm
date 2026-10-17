using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests.Support;

/// <summary>
/// Drives a <see cref="SegmentScheduler"/> with virtual workers that each move a fixed number of bytes per second, on a fake
/// clock. Each tick every worker asks for a lease if it has none and then receives its share of bytes. Used by the unit and
/// property tests so the scheduler is exercised exactly as the transfer workers will use it, without any network.
/// </summary>
internal sealed class SchedulerSimulator
{
    private readonly double[] _speed;
    private readonly double[] _carry;
    private readonly LeaseAssignment?[] _lease;
    private readonly long[] _cursor;

    public SchedulerSimulator(
        long totalBytes,
        double[] bytesPerSecondPerWorker,
        SegmentSchedulerOptions? options = null,
        bool rangesSupported = true,
        TimeSpan? tick = null,
        IEnumerable<Kasi.Core.Segment>? existingSegments = null)
    {
        Options = options ?? new SegmentSchedulerOptions { MaxConnections = bytesPerSecondPerWorker.Length };
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        Scheduler = existingSegments is null
            ? new SegmentScheduler(totalBytes, rangesSupported, Options, Time)
            : new SegmentScheduler(totalBytes, existingSegments, rangesSupported, Options, Time);
        Tick = tick ?? TimeSpan.FromMilliseconds(50);
        _speed = [.. bytesPerSecondPerWorker];
        _carry = new double[_speed.Length];
        _lease = new LeaseAssignment?[_speed.Length];
        _cursor = new long[_speed.Length];
        IdleTicks = new int[_speed.Length];
    }

    public FakeTimeProvider Time { get; }

    public SegmentScheduler Scheduler { get; }

    public SegmentSchedulerOptions Options { get; }

    public TimeSpan Tick { get; }

    /// <summary>Every lease handed out, in order.</summary>
    public List<LeaseAssignment> Issued { get; } = [];

    /// <summary>Ticks each worker spent without a lease while the download was not complete.</summary>
    public int[] IdleTicks { get; }

    /// <summary>Leases a worker stopped reading because another lease finished its segment.</summary>
    public int SupersededLeases { get; private set; }

    public long BytesReceived { get; private set; }

    public void SetSpeed(int worker, double bytesPerSecond) => _speed[worker] = bytesPerSecond;

    public LeaseAssignment? CurrentLease(int worker) => _lease[worker];

    /// <summary>One tick: every worker acquires if idle and receives bytes, then the clock moves on.</summary>
    public void Step()
    {
        for (var worker = 0; worker < _speed.Length; worker++)
        {
            if (_lease[worker] is null)
            {
                var acquired = Scheduler.TryAcquireNextLease(worker);
                if (acquired is { } lease)
                {
                    _lease[worker] = lease;
                    _cursor[worker] = lease.Start;
                    Issued.Add(lease);
                }
                else if (!Scheduler.IsComplete)
                {
                    IdleTicks[worker]++;
                }
            }

            if (_lease[worker] is { } current)
            {
                Receive(worker, current);
            }
        }

        Scheduler.AssertInvariants();
        Time.Advance(Tick);
    }

    /// <summary>Runs until the download is complete; fails the test run if it takes longer than <paramref name="limit"/> of virtual time.</summary>
    public void RunUntilComplete(TimeSpan limit)
    {
        var end = Time.GetUtcNow() + limit;
        while (!Scheduler.IsComplete)
        {
            if (Time.GetUtcNow() > end)
            {
                throw new TimeoutException($"The download was not complete after {limit} of virtual time ({Scheduler.TotalWritten} of {Scheduler.TotalBytes} bytes).");
            }

            Step();
        }
    }

    private void Receive(int worker, LeaseAssignment lease)
    {
        var budget = _carry[worker] + (_speed[worker] * Tick.TotalSeconds);
        var bytes = (long)Math.Min(budget, lease.End - _cursor[worker]);
        _carry[worker] = bytes < (lease.End - _cursor[worker]) ? budget - bytes : 0;
        if (bytes > 0)
        {
            var result = Scheduler.AdvanceLease(lease.LeaseId, (int)bytes);
            _cursor[worker] += bytes;
            BytesReceived += bytes;
            if (result.IsSuperseded)
            {
                SupersededLeases++;
                Finish(worker, lease);
                return;
            }
        }

        if (_cursor[worker] == lease.End)
        {
            Finish(worker, lease);
        }
    }

    private void Finish(int worker, LeaseAssignment lease)
    {
        Scheduler.CompleteLease(lease.LeaseId);
        _lease[worker] = null;
        _carry[worker] = 0;
    }
}
