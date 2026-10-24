namespace Kasi.Media;

/// <summary>A video quality paired with the best matching audio and subtitle rendition (TRD 7.2).</summary>
public sealed record MediaVariant(
    MediaTrack Video,
    MediaTrack? Audio,
    MediaTrack? Subtitle)
{
    public long EstimatedBytes => checked(Video.EstimatedBytes + (Audio?.EstimatedBytes ?? 0));

    public int? Height => Video.Height;

    public int? Width => Video.Width;

    public string Label => Height is { } height ? $"{height}p" : $"{Video.Bandwidth ?? 0} bps";
}
