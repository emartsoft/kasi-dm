namespace Kasi.Core;

/// <summary>One connection of a running download: the worker lane, the segment it is fetching, the range it was asked for and its speed.</summary>
public sealed record ConnectionView(int Worker, int SegmentIndex, long Start, long End, long BytesPerSecond);

/// <summary>
/// The shape of a running download, for the details window (KASI-UI-003): every segment with the bytes received so far, and each live
/// connection. Raised about four times a second while the job transfers, so the segment map moves at least twice a second. It holds
/// numbers only: no address, no path.
/// </summary>
public sealed record SegmentsProgressed(JobId Id, IReadOnlyList<Segment> Segments, IReadOnlyList<ConnectionView> Connections) : EngineEvent;

/// <summary>
/// One short line for the details window's log: what the engine decided (probe result, a retry, a split, the connection count). The text
/// is built from numbers and fixed words, never from an address, a header or a path.
/// </summary>
public sealed record JobLogged(JobId Id, DateTimeOffset At, string Line) : EngineEvent;
