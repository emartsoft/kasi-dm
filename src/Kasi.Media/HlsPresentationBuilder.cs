using System.Globalization;

namespace Kasi.Media;

/// <summary>
/// Combines a parsed HLS master with the media playlists the caller fetched. M3.2's loader owns the bounded fetch; M3.1
/// owns this pure assembly so the quality list and estimated sizes can be tested without a network.
/// </summary>
public static class HlsPresentationBuilder
{
    public static MediaPresentation Build(
        HlsMasterPlaylist master,
        Func<Uri, HlsMediaPlaylist> loadMediaPlaylist)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(loadMediaPlaylist);

        var cache = new Dictionary<string, HlsMediaPlaylist>(StringComparer.Ordinal);
        HlsMediaPlaylist Load(Uri uri)
        {
            var key = uri.AbsoluteUri;
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var playlist = loadMediaPlaylist(uri);
            cache[key] = playlist;
            return playlist;
        }

        var variants = new List<MediaVariant>();
        foreach (var variant in master.Variants.OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth))
        {
            var media = Load(variant.PlaylistUri);
            var video = ToStream(
                $"hls:{variant.PlaylistUri.AbsoluteUri}",
                MediaRole.Video,
                null,
                variant.Codecs,
                variant.Bandwidth,
                variant.Width,
                variant.Height,
                media);
            var audio = BestAudio(master, variant.AudioGroup, Load);
            var subtitle = FirstSubtitle(master, variant.SubtitleGroup, Load);
            variants.Add(new MediaVariant(video, audio, subtitle));
        }

        var audioOnly = new List<MediaTrack>();
        foreach (var rendition in master.Renditions.Where(r => r.Type == HlsRenditionType.Audio))
        {
            var media = Load(rendition.PlaylistUri);
            audioOnly.Add(ToStream(
                $"hls:audio:{rendition.PlaylistUri.AbsoluteUri}",
                MediaRole.Audio,
                rendition.Language,
                null,
                null,
                null,
                null,
                media));
        }

        var subtitles = new List<MediaTrack>();
        foreach (var rendition in master.Renditions.Where(r => r.Type == HlsRenditionType.Subtitles))
        {
            var media = Load(rendition.PlaylistUri);
            subtitles.Add(ToStream(
                $"hls:subtitle:{rendition.PlaylistUri.AbsoluteUri}",
                MediaRole.Subtitle,
                rendition.Language,
                null,
                null,
                null,
                null,
                media));
        }

        var duration = variants.Select(v => v.Video.DurationSeconds)
            .Concat(audioOnly.Select(a => a.DurationSeconds))
            .DefaultIfEmpty(0)
            .Max();
        if (variants.Select(v => v.Video.Segments.Count)
            .Concat(audioOnly.Select(a => a.Segments.Count))
            .Concat(subtitles.Select(s => s.Segments.Count))
            .Sum() > MediaLimits.MaxSegmentsTotal)
        {
            throw new MediaParseException(MediaParseError.TooManyItems, "The presentation exceeds the total segment cap.");
        }

        var live = variants.Any(v => v.Video.IsLive) || audioOnly.Any(a => a.IsLive) || subtitles.Any(s => s.IsLive);
        return new MediaPresentation(
            null,
            duration,
            live,
            [.. variants],
            [.. audioOnly.DistinctBy(a => a.Id)],
            [.. subtitles.DistinctBy(s => s.Id)]);
    }

    private static MediaTrack? BestAudio(
        HlsMasterPlaylist master,
        string? group,
        Func<Uri, HlsMediaPlaylist> load)
    {
        if (string.IsNullOrEmpty(group))
        {
            return null;
        }

        var rendition = master.Renditions
            .Where(r => r.Type == HlsRenditionType.Audio && string.Equals(r.GroupId, group, StringComparison.Ordinal))
            .OrderByDescending(r => r.Default)
            .ThenByDescending(r => r.AutoSelect)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (rendition is null)
        {
            return null;
        }

        var media = load(rendition.PlaylistUri);
        return ToStream(
            $"hls:audio:{rendition.PlaylistUri.AbsoluteUri}",
            MediaRole.Audio,
            rendition.Language,
            null,
            null,
            null,
            null,
            media);
    }

    private static MediaTrack? FirstSubtitle(
        HlsMasterPlaylist master,
        string? group,
        Func<Uri, HlsMediaPlaylist> load)
    {
        if (string.IsNullOrEmpty(group))
        {
            return null;
        }

        var rendition = master.Renditions
            .Where(r => r.Type == HlsRenditionType.Subtitles && string.Equals(r.GroupId, group, StringComparison.Ordinal))
            .OrderByDescending(r => r.Default)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (rendition is null)
        {
            return null;
        }

        var media = load(rendition.PlaylistUri);
        return ToStream(
            $"hls:subtitle:{rendition.PlaylistUri.AbsoluteUri}",
            MediaRole.Subtitle,
            rendition.Language,
            null,
            null,
            null,
            null,
            media);
    }

    private static MediaTrack ToStream(
        string id,
        MediaRole role,
        string? language,
        string? codecs,
        int? bandwidth,
        int? width,
        int? height,
        HlsMediaPlaylist media) =>
        new(
            id,
            role,
            language,
            codecs,
            null,
            bandwidth,
            width,
            height,
            media.DurationSeconds,
            media.IsLive,
            media.Initialization,
            media.Segments,
            media.Key);
}
