namespace Kasi.Core;

/// <summary>
/// A byte range <c>[Start, End)</c> of a file owned by one worker, with the number of bytes already flushed to disk
/// from <c>Start</c> (TRD 6.2, 11.2). Immutable; an invalid range cannot be constructed.
/// </summary>
public sealed record Segment
{
    public Segment(int index, long start, long end, long written = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, start);
        ArgumentOutOfRangeException.ThrowIfNegative(written);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(written, end - start);

        Index = index;
        Start = start;
        End = end;
        Written = written;
    }

    public int Index { get; }

    public long Start { get; }

    /// <summary>Exclusive.</summary>
    public long End { get; }

    public long Written { get; }

    public long Length => End - Start;

    public long Remaining => Length - Written;

    /// <summary>The offset of the next byte to fetch.</summary>
    public long Cursor => Start + Written;

    public bool IsComplete => Written == Length;

    public Segment Advance(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Remaining);
        return new Segment(Index, Start, End, Written + bytes);
    }

    /// <summary>The same segment cut short at <paramref name="newEnd"/>, as a split does; it never cuts into bytes already written.</summary>
    public Segment WithEnd(long newEnd)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(newEnd, Cursor);
        return new Segment(Index, Start, newEnd, Written);
    }
}
