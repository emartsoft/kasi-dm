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
    private readonly Random? _random;
    private readonly double _failurePerTick;

    public SchedulerSimulator(
        long totalBytes,
        double[] bytesPerSecondPerWorker,
        SegmentSchedulerOptions? options = null,
        bool rangesSupported = true,
        TimeSpan? tick = null,
        IEnumerable<Kasi.Core.Segment>? existingSegments = null,
        Random? random = null,
        double failurePerTick = 0)
    {
        _random = random;
        _failurePerTick = failurePerTick;
        // without explicit options every worker may start at once, so tests of splitting and completion are not also tests of the ramp-up
        Options = options ?? new SegmentSchedulerOptions
        {
            MaxConnections = bytesPerSecondPerWorker.Length,
            InitialConnections = bytesPerSecondPerWorker.Length,
        };
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

    /// <summary>Leases dropped by the random failure injection.</summary>
    public int Failures { get; private set; }

    public void SetSpeed(int worker, double bytesPerSecond) => _speed[worker] = bytesPerSecond;

    public LeaseAssignment? CurrentLease(int worker) => _lease[worker];

    /// <summary>
    /// One tick: each worker spends its byte budget for the tick, finishing leases and starting the next ones within the same tick
    /// (a fast connection is never idle between leases), then the clock moves on.
    /// </summary>
    public void Step()
    {
        for (var worker = 0; worker < _speed.Length; worker++)
        {
            var budget = _carry[worker] + (_speed[worker] * Tick.TotalSeconds);
            var failureChecked = false;
            while (true)
            {
                if (_lease[worker] is null)
                {
                    var acquired = Scheduler.TryAcquireNextLease(worker);
                    if (acquired is not { } lease)
                    {
                        if (!Scheduler.IsComplete)
                        {
                            IdleTicks[worker]++;
                        }

                        budget = 0;
                        break;
                    }

                    _lease[worker] = lease;
                    _cursor[worker] = lease.Start;
                    Issued.Add(lease);
                }

                var current = _lease[worker]!.Value;
                if (!failureChecked && _random is not null && _random.NextDouble() < _failurePerTick)
                {
                    // the connection drops: the bytes received so far stay counted and the lease is given up
                    Scheduler.AbandonLease(current.LeaseId);
                    _lease[worker] = null;
                    Failures++;
                    budget = 0;
                    break;
                }

                failureChecked = true;
                var take = (long)Math.Min(budget, current.End - _cursor[worker]);
                var superseded = false;
                if (take > 0)
                {
                    superseded = Scheduler.AdvanceLease(current.LeaseId, (int)take).IsSuperseded;
                    _cursor[worker] += take;
                    BytesReceived += take;
                    budget -= take;
                }

                if (superseded)
                {
                    SupersededLeases++;
                }

                if (superseded || _cursor[worker] == current.End)
                {
                    Scheduler.CompleteLease(current.LeaseId);
                    _lease[worker] = null;
                    if (budget >= 1)
                    {
                        continue; // still has bytes to spend this tick: start the next lease at once
                    }
                }

                break;
            }

            _carry[worker] = _lease[worker] is null ? 0 : budget;
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
}
