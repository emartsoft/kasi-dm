namespace Kasi.Engine;

/// <summary>
/// One bounded byte-range request a worker should make: <c>Range: bytes=Start-(End-1)</c>. A lease never extends past the end of
/// its segment and a split never cuts into it (TRD 6.2).
/// </summary>
/// <param name="LeaseId">Unique and increasing within one scheduler.</param>
/// <param name="WorkerId">The worker lane, <c>0 .. MaxConnections - 1</c>.</param>
/// <param name="SegmentIndex">The <see cref="Kasi.Core.Segment.Index"/> being fetched.</param>
/// <param name="Start">First byte, inclusive.</param>
/// <param name="End">One past the last byte.</param>
/// <param name="IsEndgameDuplicate">True when this lease shadows a segment another worker is still fetching (TRD 6.3).</param>
/// <param name="IsInitialProbeStream">True for worker 0's first lease when it is the response the probe already holds open.</param>
public readonly record struct LeaseAssignment(
    long LeaseId,
    int WorkerId,
    int SegmentIndex,
    long Start,
    long End,
    bool IsEndgameDuplicate = false,
    bool IsInitialProbeStream = false)
{
    public long Length => End - Start;
}
