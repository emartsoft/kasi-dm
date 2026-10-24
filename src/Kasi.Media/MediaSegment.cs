namespace Kasi.Media;

/// <summary>
/// One whole media segment: the unit the M3.3 downloader fetches and marks complete. <see cref="Sequence"/> is the HLS media
/// sequence number (EXT-X-MEDIA-SEQUENCE + index), the default AES-128 IV when the key has none (RFC 8216 4.3.2.4).
/// </summary>
public sealed record MediaSegment(
    int Index,
    Uri Uri,
    double DurationSeconds,
    MediaByteRange? Range = null,
    MediaKey? Key = null,
    bool Discontinuity = false,
    string? Title = null,
    long? Sequence = null)
{
    public long? KnownBytes => Range?.Length;
}
