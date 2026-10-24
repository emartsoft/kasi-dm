namespace Kasi.Media;

/// <summary>One whole media segment: the unit the M3.3 downloader fetches and marks complete.</summary>
public sealed record MediaSegment(
    int Index,
    Uri Uri,
    double DurationSeconds,
    MediaByteRange? Range = null,
    MediaKey? Key = null,
    bool Discontinuity = false,
    string? Title = null)
{
    public long? KnownBytes => Range?.Length;
}
