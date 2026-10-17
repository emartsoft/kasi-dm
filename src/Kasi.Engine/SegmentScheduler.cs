using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// Hands out bounded byte-range leases over the segments of one download and splits segments when a worker runs out of
/// work (TRD 6.2, 6.3; ADR 0004). A pure, thread-safe state machine: no I/O, time only through <see cref="TimeProvider"/>.
/// Workers are numbered <c>0 .. MaxConnections - 1</c> and hold at most one lease at a time.
/// </summary>
public sealed class SegmentScheduler
{
    private readonly Lock _gate = new();
    private readonly SegmentSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly bool _rangesSupported;
    private readonly bool _firstLeaseIsProbeStream;
    private readonly List<SegmentState> _segments = [];
    private readonly Dictionary<long, Lease> _leases = [];
    private readonly Lease?[] _workerLease;
    private readonly SegmentState?[] _workerSegment;
    private readonly SpeedTracker[] _speeds;
    private long _nextLeaseId = 1;
    private int _nextSegmentIndex;
    private int _incompleteSegments;
    private long _totalWritten;
    private int _structureVersion;
    private int _allowedConnections;
    private int _connectionCap;
    private bool _rampStopped;
    private bool _newestSeen;
    private DateTimeOffset _rampWindowStart;
    private long _rampWindowBytes;
    private double? _previousRate;
    private bool _firstLeaseIssued;

    /// <summary>Starts a fresh download: one segment covering the whole file (none for an empty file).</summary>
    /// <param name="totalBytes">The size of the file; zero is complete from the start.</param>
    /// <param name="rangesSupported">False means one segment and one worker, never split.</param>
    /// <param name="firstLeaseIsProbeStream">When true, worker 0's first lease is flagged as the response the probe holds open.</param>
    public SegmentScheduler(
        long totalBytes,
        bool rangesSupported = true,
        SegmentSchedulerOptions? options = null,
        TimeProvider? timeProvider = null,
        bool firstLeaseIsProbeStream = false)
        : this(totalBytes, rangesSupported, options, timeProvider, firstLeaseIsProbeStream, existingSegments: null)
    {
    }

    /// <summary>Restores a download from persisted segments (resume); they must partition <c>[0, totalBytes)</c> exactly.</summary>
    public SegmentScheduler(
        long totalBytes,
        IEnumerable<Segment> existingSegments,
        bool rangesSupported = true,
        SegmentSchedulerOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(totalBytes, rangesSupported, options, timeProvider, firstLeaseIsProbeStream: false, existingSegments ?? throw new ArgumentNullException(nameof(existingSegments)))
    {
    }

    private SegmentScheduler(
        long totalBytes,
        bool rangesSupported,
        SegmentSchedulerOptions? options,
        TimeProvider? timeProvider,
        bool firstLeaseIsProbeStream,
        IEnumerable<Segment>? existingSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        _options = options ?? new SegmentSchedulerOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _rangesSupported = rangesSupported;
        _firstLeaseIsProbeStream = firstLeaseIsProbeStream;
        TotalBytes = totalBytes;

        _workerLease = new Lease?[_options.MaxConnections];
        _workerSegment = new SegmentState?[_options.MaxConnections];
        _speeds = Enumerable.Range(0, _options.MaxConnections).Select(_ => new SpeedTracker()).ToArray();

        if (existingSegments is null)
        {
            if (totalBytes > 0)
            {
                _segments.Add(new SegmentState(0, 0, totalBytes, 0));
            }
        }
        else
        {
            foreach (var segment in ValidatePartition(totalBytes, existingSegments))
            {
                _segments.Add(new SegmentState(segment.Index, segment.Start, segment.End, segment.Written));
            }
        }

        _nextSegmentIndex = _segments.Count == 0 ? 0 : _segments.Max(s => s.Index) + 1;
        _incompleteSegments = _segments.Count(s => !s.IsComplete);
        _totalWritten = _segments.Sum(s => s.Written);
        _connectionCap = _options.MaxConnections;
        _allowedConnections = rangesSupported ? _options.InitialConnections : 1;
    }

    public long TotalBytes { get; }

    public bool RangesSupported => _rangesSupported;

    /// <summary>How many workers may hold a lease right now.</summary>
    public int AllowedConnections
    {
        get
        {
            lock (_gate)
            {
                return _allowedConnections;
            }
        }
    }

    /// <summary>Bytes received so far (not necessarily flushed; see ADR 0004 for what may be checkpointed).</summary>
    public long TotalWritten
    {
        get
        {
            lock (_gate)
            {
                return _totalWritten;
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _incompleteSegments == 0;
            }
        }
    }

    /// <summary>Raised by one whenever a segment is split. The engine persists the snapshot when it changes (ADR 0004).</summary>
    public int StructureVersion
    {
        get
        {
            lock (_gate)
            {
                return _structureVersion;
            }
        }
    }

    /// <summary>The segments ordered by start offset, with the bytes received so far.</summary>
    public IReadOnlyList<Segment> GetSegmentsSnapshot()
    {
        lock (_gate)
        {
            return [.. _segments.Select(s => new Segment(s.Index, s.Start, s.End, s.Written))];
        }
    }

    /// <summary>
    /// Gives an idle worker its next lease, or null if there is nothing for it: the download is complete, the worker may not
    /// transfer yet, it already holds a lease, or every remaining byte is being fetched and cannot usefully be split.
    /// </summary>
    public LeaseAssignment? TryAcquireNextLease(int workerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(workerId, _options.MaxConnections);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            EvaluateRampUpCore(now);
            if (_incompleteSegments == 0 || workerId >= _allowedConnections || _workerLease[workerId] is not null)
            {
                return null;
            }

            // 1. keep going on the segment this worker already owns
            var own = _workerSegment[workerId];
            if (own is not null)
            {
                if (!own.IsComplete)
                {
                    return IssuePrimary(workerId, own, now);
                }

                ReleaseOwnership(own);
            }

            // 2. a segment nobody owns (the start of the file, or one released after a failure)
            foreach (var segment in _segments)
            {
                if (!segment.IsComplete && segment.Owner < 0)
                {
                    return IssuePrimary(workerId, segment, now);
                }
            }

            // 3. split the segment with the most bytes not yet leased (TRD 6.3 TrySplit)
            if (_rangesSupported && TryPickSplit(out var victim, out var leaseEnd))
            {
                return IssuePrimary(workerId, Split(victim, leaseEnd), now);
            }

            return null;
        }
    }

    /// <summary>
    /// Records <paramref name="bytesWritten"/> received for a lease, in order from its cursor. The segment's contiguous prefix
    /// advances by whatever was not already covered; <see cref="LeaseAdvanceResult.IsSuperseded"/> tells the worker another lease
    /// finished the segment first.
    /// </summary>
    public LeaseAdvanceResult AdvanceLease(long leaseId, int bytesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten);

        lock (_gate)
        {
            var lease = Find(leaseId);
            var segment = lease.Segment;
            if (lease.Superseded || segment.IsComplete)
            {
                lease.Superseded = true;
                return new LeaseAdvanceResult(segment.Cursor, 0, lease.Cursor == lease.End, IsSuperseded: true);
            }

            ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesWritten, lease.End - lease.Cursor);

            var now = _time.GetUtcNow();
            _speeds[lease.Worker].Add(bytesWritten, now);
            CountForRampUp(lease.Worker, bytesWritten, now);

            var before = segment.Cursor;
            lease.Cursor += bytesWritten;
            var gain = Math.Max(0, lease.Cursor - before);
            if (gain > 0)
            {
                segment.Written += gain;
                _totalWritten += gain;
                if (segment.IsComplete)
                {
                    _incompleteSegments--;
                    segment.SupersedeOthers(lease);
                }
            }

            EvaluateRampUpCore(now);
            return new LeaseAdvanceResult(before, gain, lease.Cursor == lease.End, IsSuperseded: false);
        }
    }

    /// <summary>
    /// Adds a connection when it is time (TRD 6.3): after the first 250 ms of transfer one is added; after that one more each 250 ms
    /// window while the connection added last raised aggregate throughput by at least 5% of what an average existing connection
    /// moves (ADR 0004: a flat 5% of the total cannot be met beyond 20 connections even on a link that scales perfectly, which would
    /// make TC-ENG-006 unreachable at 32). The first window that falls short ends the ramp-up for this download. Also runs from
    /// <see cref="TryAcquireNextLease"/> and <see cref="AdvanceLease"/>.
    /// </summary>
    /// <returns>How many connections are allowed now.</returns>
    public int EvaluateRampUp()
    {
        lock (_gate)
        {
            EvaluateRampUpCore(_time.GetUtcNow());
            return _allowedConnections;
        }
    }

    /// <summary>
    /// The host is throttling (429, 503 with Retry-After): lower the connection limit and stop ramping up. Workers above the new
    /// limit finish their current lease and get no more; their segments are released for the others.
    /// </summary>
    public void ReduceAllowedConnections(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        lock (_gate)
        {
            _connectionCap = Math.Max(1, _connectionCap - count);
            _allowedConnections = Math.Max(1, Math.Min(_allowedConnections, _connectionCap));
            _rampStopped = true;
            for (var worker = _allowedConnections; worker < _workerSegment.Length; worker++)
            {
                ReleaseIfOverLimit(worker);
            }
        }
    }

    /// <summary>
    /// The lease was read to its end (or was superseded). The worker keeps its segment and its next lease continues it.
    /// A lease that stopped early must be given to <see cref="AbandonLease"/> instead.
    /// </summary>
    public void CompleteLease(long leaseId)
    {
        lock (_gate)
        {
            var lease = Find(leaseId);
            if (lease.Cursor != lease.End && !lease.Superseded && !lease.Segment.IsComplete)
            {
                throw new InvalidOperationException("The lease was not read to its end; use AbandonLease for a lease that stopped early.");
            }

            Detach(lease);
            if (lease.Segment.IsComplete)
            {
                ReleaseOwnership(lease.Segment);
            }

            ReleaseIfOverLimit(lease.Worker);
        }
    }

    /// <summary>
    /// The lease stopped early (a failure or a cancel). Bytes already received stay counted; the segment is released and the next lease
    /// for it starts at its cursor.
    /// </summary>
    public void AbandonLease(long leaseId)
    {
        lock (_gate)
        {
            var lease = Find(leaseId);
            var segment = lease.Segment;
            var wasPrimary = !lease.IsShadow;
            Detach(lease);

            if (segment.IsComplete)
            {
                ReleaseOwnership(segment);
            }
            else if (wasPrimary)
            {
                ReleaseOwnership(segment);
                PromoteShadow(segment);
            }
        }
    }

    private bool RampUpActive => _rangesSupported && !_rampStopped && _allowedConnections < _connectionCap;

    private void CountForRampUp(int worker, int bytes, DateTimeOffset now)
    {
        if (!RampUpActive)
        {
            return;
        }

        if (!_newestSeen && worker == _allowedConnections - 1)
        {
            // the connection added last has just started: measure from here so its start-up time is not held against it
            _newestSeen = true;
            _rampWindowStart = now;
            _rampWindowBytes = 0;
        }

        if (_newestSeen)
        {
            _rampWindowBytes += bytes;
        }
    }

    private void EvaluateRampUpCore(DateTimeOffset now)
    {
        if (!RampUpActive || !_newestSeen || now - _rampWindowStart < _options.RampUpInterval)
        {
            return;
        }

        var rate = _rampWindowBytes / (now - _rampWindowStart).TotalSeconds;
        if (_previousRate is { } previous && rate - previous < _options.RampUpThroughputGainThreshold * previous / (_allowedConnections - 1))
        {
            _rampStopped = true; // the last connection added did not raise throughput enough: this is as many as this server gives
            return;
        }

        if (!HasSplittableWork())
        {
            _rampWindowStart = now;
            _rampWindowBytes = 0;
            return; // nothing for another connection to do; look again next window
        }

        _previousRate = rate;
        _allowedConnections++;
        _newestSeen = false;
    }

    /// <summary>True when another connection would have something to fetch: an unowned segment or one with enough unleased bytes to split.</summary>
    private bool HasSplittableWork()
    {
        foreach (var segment in _segments)
        {
            if (segment.IsComplete)
            {
                continue;
            }

            if (segment.Owner < 0 || segment.End - segment.LeaseEnd >= 2 * _options.MinSplitSize)
            {
                return true;
            }
        }

        return false;
    }

    private void ReleaseIfOverLimit(int worker)
    {
        if (worker >= _allowedConnections && _workerLease[worker] is null && _workerSegment[worker] is { } segment)
        {
            ReleaseOwnership(segment);
        }
    }

    private LeaseAssignment IssuePrimary(int workerId, SegmentState segment, DateTimeOffset now)
    {
        var start = segment.Cursor;
        long end;
        var isProbe = _firstLeaseIsProbeStream && !_firstLeaseIssued && workerId == 0 && start == 0;
        if (isProbe)
        {
            // exactly the range the probe asked for, so its response is this lease and ends by itself
            end = Math.Min(segment.End, _options.MinLeaseBytes);
        }
        else
        {
            var speed = _speeds[workerId].Speed(now);
            end = Math.Min(segment.End, start + _options.LeaseSizeFor(speed));
            if (segment.End - end < _options.MinLeaseBytes)
            {
                end = segment.End; // no tiny trailing request
            }
        }

        var lease = new Lease(_nextLeaseId++, workerId, segment, start, end, isShadow: false);
        Attach(lease, now);
        segment.Owner = workerId;
        _workerSegment[workerId] = segment;
        _firstLeaseIssued = true;
        return new LeaseAssignment(lease.Id, workerId, segment.Index, start, end, IsEndgameDuplicate: false, IsInitialProbeStream: isProbe);
    }

    private void Attach(Lease lease, DateTimeOffset now)
    {
        _leases.Add(lease.Id, lease);
        _workerLease[lease.Worker] = lease;
        _speeds[lease.Worker].Resume(now);
        if (lease.IsShadow)
        {
            lease.Segment.Shadow = lease;
        }
        else
        {
            lease.Segment.Primary = lease;
        }
    }

    private void Detach(Lease lease)
    {
        _leases.Remove(lease.Id);
        _workerLease[lease.Worker] = null;
        _speeds[lease.Worker].Idle(_time.GetUtcNow());
        if (lease.IsShadow)
        {
            lease.Segment.Shadow = null;
        }
        else
        {
            lease.Segment.Primary = null;
        }
    }

    private void ReleaseOwnership(SegmentState segment)
    {
        if (segment.Owner >= 0)
        {
            _workerSegment[segment.Owner] = null;
            segment.Owner = -1;
        }
    }

    /// <summary>When the primary lease of a segment fails while an endgame duplicate is still running, the duplicate takes over.</summary>
    private void PromoteShadow(SegmentState segment)
    {
        if (segment.Shadow is { } shadow)
        {
            shadow.IsShadow = false;
            segment.Shadow = null;
            segment.Primary = shadow;
            segment.Owner = shadow.Worker;
            _workerSegment[shadow.Worker] = segment;
        }
    }

    private bool TryPickSplit(out SegmentState victim, out long leaseEnd)
    {
        victim = null!;
        leaseEnd = 0;
        var bestFree = 0L;
        foreach (var segment in _segments)
        {
            if (segment.IsComplete)
            {
                continue;
            }

            var free = segment.End - segment.LeaseEnd;
            if (free > bestFree)
            {
                bestFree = free;
                victim = segment;
                leaseEnd = segment.LeaseEnd;
            }
        }

        return victim is not null && bestFree >= 2 * _options.MinSplitSize;
    }

    /// <summary>Shrinks <paramref name="victim"/> at the split point and returns the new segment for the idle worker. Never cuts into the lease in flight.</summary>
    private SegmentState Split(SegmentState victim, long leaseEnd)
    {
        var free = victim.End - leaseEnd;
        var mid = AlignUp(leaseEnd + (free / 2), _options.PageAlignment);
        if (mid <= leaseEnd || mid >= victim.End)
        {
            mid = leaseEnd + (free / 2); // alignment would leave nothing: split unaligned
        }

        var fresh = new SegmentState(_nextSegmentIndex++, mid, victim.End, 0);
        victim.End = mid;
        _segments.Insert(_segments.IndexOf(victim) + 1, fresh);
        _incompleteSegments++;
        _structureVersion++;
        return fresh;
    }

    private Lease Find(long leaseId) =>
        _leases.TryGetValue(leaseId, out var lease)
            ? lease
            : throw new InvalidOperationException($"There is no active lease {leaseId}.");

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static List<Segment> ValidatePartition(long totalBytes, IEnumerable<Segment> existing)
    {
        var ordered = existing.OrderBy(s => s.Start).ToList();
        if (totalBytes == 0)
        {
            return ordered.Count == 0
                ? ordered
                : throw new ArgumentException("An empty file cannot have segments.", nameof(existing));
        }

        long expectedStart = 0;
        foreach (var segment in ordered)
        {
            if (segment.Start != expectedStart)
            {
                throw new ArgumentException(
                    $"Segments must partition the file with no gap or overlap; segment {segment.Index} starts at {segment.Start} but {expectedStart} was expected.",
                    nameof(existing));
            }

            expectedStart = segment.End;
        }

        if (expectedStart != totalBytes)
        {
            throw new ArgumentException($"Segments end at {expectedStart} but the file has {totalBytes} bytes.", nameof(existing));
        }

        if (ordered.Select(s => s.Index).Distinct().Count() != ordered.Count)
        {
            throw new ArgumentException("Segment indexes must be unique.", nameof(existing));
        }

        return ordered;
    }

    /// <summary>Checks the internal invariants; for tests and diagnostics. Throws <see cref="InvalidOperationException"/> naming the first violation.</summary>
    internal void AssertInvariants()
    {
        lock (_gate)
        {
            long expectedStart = 0;
            long written = 0;
            var incomplete = 0;
            foreach (var segment in _segments)
            {
                Check(segment.Start == expectedStart, $"segment {segment.Index} starts at {segment.Start}, expected {expectedStart}");
                Check(segment.End > segment.Start, $"segment {segment.Index} is empty or inverted");
                Check(segment.Written >= 0 && segment.Written <= segment.Length, $"segment {segment.Index} has written {segment.Written} of {segment.Length}");
                expectedStart = segment.End;
                written += segment.Written;
                incomplete += segment.IsComplete ? 0 : 1;

                if (segment.Primary is { } primary)
                {
                    Check(primary.Segment == segment && !primary.IsShadow, $"segment {segment.Index} primary lease is inconsistent");
                    Check(primary.Start >= segment.Start && primary.End <= segment.End, $"lease {primary.Id} [{primary.Start},{primary.End}) leaves segment {segment.Index} [{segment.Start},{segment.End})");
                    Check(segment.Owner == primary.Worker, $"segment {segment.Index} is owned by {segment.Owner} but leased to {primary.Worker}");
                }

                if (segment.Shadow is { } shadow)
                {
                    Check(shadow.Segment == segment && shadow.IsShadow, $"segment {segment.Index} shadow lease is inconsistent");
                    Check(shadow.End <= segment.End, $"shadow lease {shadow.Id} leaves segment {segment.Index}");
                }

                if (segment.Owner >= 0)
                {
                    Check(_workerSegment[segment.Owner] == segment, $"worker {segment.Owner} does not point back at segment {segment.Index}");
                }
            }

            Check(expectedStart == TotalBytes, $"segments end at {expectedStart}, the file has {TotalBytes}");
            Check(written == _totalWritten, $"written {written} differs from the counter {_totalWritten}");
            Check(incomplete == _incompleteSegments, $"{incomplete} incomplete segments differ from the counter {_incompleteSegments}");
            for (var worker = 0; worker < _workerLease.Length; worker++)
            {
                if (_workerLease[worker] is { } lease)
                {
                    Check(lease.Worker == worker && _leases.ContainsKey(lease.Id), $"worker {worker} lease table is inconsistent");
                }

                if (_workerSegment[worker] is { } owned)
                {
                    Check(owned.Owner == worker, $"worker {worker} claims segment {owned.Index} owned by {owned.Owner}");
                }
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Scheduler invariant violated: " + message);
            }
        }
    }

    private sealed class SegmentState(int index, long start, long end, long written)
    {
        public int Index { get; } = index;

        public long Start { get; } = start;

        public long End { get; set; } = end;

        public long Written { get; set; } = written;

        public int Owner { get; set; } = -1;

        public Lease? Primary { get; set; }

        public Lease? Shadow { get; set; }

        public long Length => End - Start;

        public long Cursor => Start + Written;

        public bool IsComplete => Written == Length;

        /// <summary>The end of what is already being fetched: the primary lease, or the cursor when none is in flight.</summary>
        public long LeaseEnd => Primary is { } lease ? Math.Max(lease.End, Cursor) : Cursor;

        public void SupersedeOthers(Lease winner)
        {
            if (Primary is { } primary && primary != winner)
            {
                primary.Superseded = true;
            }

            if (Shadow is { } shadow && shadow != winner)
            {
                shadow.Superseded = true;
            }
        }
    }

    private sealed class Lease(long id, int worker, SegmentState segment, long start, long end, bool isShadow)
    {
        public long Id { get; } = id;

        public int Worker { get; } = worker;

        public SegmentState Segment { get; } = segment;

        public long Start { get; } = start;

        public long End { get; } = end;

        public long Cursor { get; set; } = start;

        public bool IsShadow { get; set; } = isShadow;

        public bool Superseded { get; set; }
    }
}
