namespace Kasi.Engine;

/// <summary>What recording received bytes did to the segment (see <see cref="SegmentScheduler.AdvanceLease"/>).</summary>
/// <param name="AcceptedStart">First byte that newly advanced the segment's contiguous prefix.</param>
/// <param name="AcceptedLength">Bytes that newly advanced it; zero when an endgame peer had already written them.</param>
/// <param name="IsLeaseComplete">True when the lease has reached its end.</param>
/// <param name="IsSuperseded">True when the segment was finished by another lease; the worker may stop reading this one.</param>
public readonly record struct LeaseAdvanceResult(long AcceptedStart, long AcceptedLength, bool IsLeaseComplete, bool IsSuperseded);
