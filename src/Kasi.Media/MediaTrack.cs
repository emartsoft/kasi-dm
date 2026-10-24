namespace Kasi.Media;

/// <summary>
/// One selected rendition of a presentation: a concrete, ordered segment list plus the metadata the quality list shows.
/// The HLS/DASH parsers produce it; the M3.2 protection gate decides whether it may be downloaded.
/// </summary>
public sealed record MediaTrack(
    string Id,
    MediaRole Role,
    string? Language,
    string? Codecs,
    string? MimeType,
    int? Bandwidth,
    int? Width,
    int? Height,
    double DurationSeconds,
    bool IsLive,
    MediaInitSegment? Initialization,
    IReadOnlyList<MediaSegment> Segments,
    MediaKey? Key = null,
    IReadOnlyList<string>? ProtectionSchemes = null)
{
    public long EstimatedBytes => MediaSizes.Estimate(Bandwidth, DurationSeconds);

    public bool IsComplete => !IsLive && Segments.Count > 0;

    public IEnumerable<string> ProtectionSignals
    {
        get
        {
            foreach (var scheme in ProtectionSchemes ?? [])
            {
                yield return scheme;
            }

            if (Key is { } key)
            {
                yield return key.Method;
                if (key.KeyFormat is { } format)
                {
                    yield return format;
                }
            }
        }
    }
}
