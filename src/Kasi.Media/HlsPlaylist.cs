namespace Kasi.Media;

/// <summary>An HLS master playlist (RFC 8216 §4.3.4).</summary>
public sealed record HlsMasterPlaylist(
    Uri ManifestUri,
    IReadOnlyList<HlsVariant> Variants,
    IReadOnlyList<HlsRendition> Renditions,
    IReadOnlyList<MediaKey> SessionKeys);

/// <summary>One <c>EXT-X-STREAM-INF</c> and the media playlist it points at.</summary>
public sealed record HlsVariant(
    Uri PlaylistUri,
    int Bandwidth,
    int? AverageBandwidth,
    int? Width,
    int? Height,
    string? Codecs,
    string? AudioGroup,
    string? SubtitleGroup,
    double? FrameRate,
    string? VideoRange);

public enum HlsRenditionType
{
    Audio,
    Subtitles,
}

/// <summary>One <c>EXT-X-MEDIA</c> rendition with its own media playlist URI.</summary>
public sealed record HlsRendition(
    HlsRenditionType Type,
    string GroupId,
    string Name,
    string? Language,
    bool Default,
    bool AutoSelect,
    bool Forced,
    Uri PlaylistUri,
    string? Channels,
    string? Characteristics)
{
    public MediaRole Role => Type == HlsRenditionType.Audio ? MediaRole.Audio : MediaRole.Subtitle;
}

/// <summary>The media playlist for one rendition.</summary>
public sealed record HlsMediaPlaylist(
    Uri PlaylistUri,
    double TargetDurationSeconds,
    int MediaSequence,
    double DurationSeconds,
    bool IsLive,
    MediaInitSegment? Initialization,
    IReadOnlyList<MediaSegment> Segments,
    MediaKey? Key = null);
