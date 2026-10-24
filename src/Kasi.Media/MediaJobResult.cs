namespace Kasi.Media;

public enum MediaJobErrorKind
{
    InvalidJob,
    Network,
    DiskFull,
    Disk,
    SegmentMismatch,
}

/// <summary>A media capture failed before muxing. The message never contains a segment URL or query string.</summary>
public sealed class MediaJobException : Exception
{
    public MediaJobException(MediaJobErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public MediaJobErrorKind Kind { get; }
}

/// <summary>One completed segment, reported after its file is flushed and renamed.</summary>
public sealed record MediaJobProgress(int CompletedSegments, int TotalSegments, long DoneBytes);

/// <summary>One stream joined in order (init first, then segments) and ready for the M3.4 mux step.</summary>
public sealed record MediaJoinedTrack(int Index, string Role, string Path, long Bytes, string? Container);

/// <summary>The result of a media download; the job is left in <see cref="Kasi.Core.JobState.Muxing"/> for M3.4.</summary>
public sealed record MediaJobResult(
    Kasi.Core.JobId JobId,
    IReadOnlyList<MediaJoinedTrack> Streams,
    long DoneBytes,
    MediaVariant? Variant = null,
    string? WorkDirectory = null)
{
    public double DurationSeconds => Variant?.Video.DurationSeconds ?? 0;
}
