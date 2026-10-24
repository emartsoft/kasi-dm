namespace Kasi.Media;

/// <summary>
/// The neutral result of parsing one HLS master or DASH MPD (M3.1). It contains only manifest-derived data; no segment has
/// been fetched. Variants are highest quality first, each with its best matching audio and optional subtitle.
/// </summary>
public sealed record MediaPresentation(
    string? Title,
    double DurationSeconds,
    bool IsLive,
    IReadOnlyList<MediaVariant> Variants,
    IReadOnlyList<MediaTrack> AudioOnly,
    IReadOnlyList<MediaTrack> Subtitles)
{
    public IReadOnlyList<MediaTrack> Streams
    {
        get
        {
            var streams = new List<MediaTrack>();
            foreach (var variant in Variants)
            {
                streams.Add(variant.Video);
                if (variant.Audio is { } audio)
                {
                    streams.Add(audio);
                }

                if (variant.Subtitle is { } subtitle)
                {
                    streams.Add(subtitle);
                }
            }

            streams.AddRange(AudioOnly);
            streams.AddRange(Subtitles);
            return [.. streams.DistinctBy(stream => stream.Id)];
        }
    }

    /// <summary>
    /// The default is the highest video at or below <paramref name="preferredMaximumHeight"/> (1080p by default), falling back
    /// to the highest video when every variant is above the limit. A presentation with no video returns null.
    /// </summary>
    public MediaVariant? SelectDefault(int preferredMaximumHeight = 1080)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredMaximumHeight);
        if (Variants.Count == 0)
        {
            return null;
        }

        var atOrBelow = Variants
            .Where(variant => variant.Height is not { } height || height <= preferredMaximumHeight)
            .OrderByDescending(variant => variant.Height ?? 0)
            .ThenByDescending(variant => variant.Video.Bandwidth ?? 0)
            .FirstOrDefault();
        return atOrBelow ?? Variants[0];
    }

    /// <summary>The variant a saved choice means: same height, nearest bitrate. Null when the stream no longer offers that height.</summary>
    public MediaVariant? FindVariant(int? height, int? bandwidth) =>
        Variants
            .Where(variant => variant.Height == height)
            .OrderBy(variant => Math.Abs((variant.Video.Bandwidth ?? 0) - (bandwidth ?? 0)))
            .FirstOrDefault();

    /// <summary>The audio-only track a saved choice means: same language, nearest bitrate; null when there is none.</summary>
    public MediaTrack? FindAudioOnly(string? language, int? bandwidth) =>
        AudioOnly
            .Where(track => string.Equals(track.Language, language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(track => Math.Abs((track.Bandwidth ?? 0) - (bandwidth ?? 0)))
            .FirstOrDefault();

    public static MediaPresentation Empty { get; } = new(null, 0, false, [], [], []);
}

/// <summary>Size estimates use the declared bitrate and duration; they are approximate by construction.</summary>
public static class MediaSizes
{
    public static long Estimate(int? bitsPerSecond, double durationSeconds)
    {
        if (bitsPerSecond is not { } bandwidth || bandwidth <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return 0;
        }

        return checked((long)Math.Round(bandwidth * durationSeconds / 8.0, MidpointRounding.AwayFromZero));
    }
}
