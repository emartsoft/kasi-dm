namespace Kasi.Engine;

/// <summary>A connection that is fetching a range right now: the worker lane, the segment, the range asked for and how far it has got.</summary>
public readonly record struct LeaseSnapshot(int Worker, int SegmentIndex, long Start, long End, long Cursor);
