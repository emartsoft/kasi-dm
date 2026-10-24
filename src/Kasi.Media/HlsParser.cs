using System.Globalization;
using System.Text;

namespace Kasi.Media;

/// <summary>
/// Hand-written RFC 8216 parser. It never fetches anything; it turns master and media playlist text into the neutral model.
/// Every malformed/hostile input ends in <see cref="MediaParseException"/> and every collection is capped.
/// </summary>
public static class HlsParser
{
    public static HlsMasterPlaylist ParseMaster(string? text, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        try
        {
            if (!manifestUri.IsAbsoluteUri)
            {
                throw Error(MediaParseError.InvalidUri, "The manifest URI must be absolute.");
            }

            var lines = Lines(text);
            var variants = new List<HlsVariant>();
            var renditions = new List<HlsRendition>();
            var sessionKeys = new List<MediaKey>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    if (variants.Count >= MediaLimits.MaxVariants)
                    {
                        throw Error(MediaParseError.TooManyItems, "The master playlist has too many variants.");
                    }

                    var attributes = Attributes(line["#EXT-X-STREAM-INF:".Length..], line);
                    var uri = NextUri(lines, ref i, manifestUri);
                    variants.Add(ToVariant(attributes, uri, line));
                }
                else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    if (renditions.Count >= MediaLimits.MaxRenditions)
                    {
                        throw Error(MediaParseError.TooManyItems, "The master playlist has too many renditions.");
                    }

                    if (ToRendition(Attributes(line["#EXT-X-MEDIA:".Length..], line), manifestUri, line) is { } rendition)
                    {
                        renditions.Add(rendition);
                    }
                }
                else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                {
                    sessionKeys.Add(ToKey(Attributes(line["#EXT-X-SESSION-KEY:".Length..], line), manifestUri, line));
                }
                else if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal))
                {
                    // I-frame-only renditions are not downloadable qualities; the plan explicitly ignores them.
                }
                else if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
                {
                    throw Error(MediaParseError.InvalidPlaylist, "A media playlist cannot be parsed as a master playlist.");
                }
                else if (!line.StartsWith('#'))
                {
                    throw Error(MediaParseError.InvalidPlaylist, "A URI line has no EXT-X-STREAM-INF.");
                }
            }

            return new HlsMasterPlaylist(manifestUri, variants, renditions, sessionKeys);
        }
        catch (MediaParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MediaParseException(MediaParseError.InvalidPlaylist, "The HLS master playlist is invalid.", ex);
        }
    }

    public static HlsMediaPlaylist ParseMedia(string? text, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(playlistUri);
        try
        {
            if (!playlistUri.IsAbsoluteUri)
            {
                throw Error(MediaParseError.InvalidUri, "The playlist URI must be absolute.");
            }

            var lines = Lines(text);
            var segments = new List<MediaSegment>();
            var duration = 0.0;
            var targetDuration = 0.0;
            var mediaSequence = 0;
            var endList = false;
            MediaInitSegment? initialization = null;
            MediaKey? currentKey = null;
            MediaKey? firstKey = null;
            double? pendingDuration = null;
            string? pendingTitle = null;
            MediaByteRange? pendingRange = null;
            var discontinuity = false;
            long? lastRangeEnd = null;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                {
                    targetDuration = PositiveDouble(line["#EXT-X-TARGETDURATION:".Length..], line);
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    mediaSequence = NonNegativeInt(line["#EXT-X-MEDIA-SEQUENCE:".Length..], line);
                }
                else if (line == "#EXT-X-ENDLIST")
                {
                    endList = true;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    var key = ToKey(Attributes(line["#EXT-X-KEY:".Length..], line), playlistUri, line);
                    currentKey = key.Method == "NONE" ? null : key;
                    firstKey ??= currentKey;
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    initialization = ToInit(Attributes(line["#EXT-X-MAP:".Length..], line), playlistUri, line);
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var value = line["#EXTINF:".Length..];
                    var comma = value.IndexOf(',', StringComparison.Ordinal);
                    var durationText = comma >= 0 ? value[..comma] : value;
                    pendingDuration = NonNegativeDouble(durationText, line);
                    pendingTitle = comma >= 0 ? value[(comma + 1)..].Trim() : null;
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    pendingRange = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..], ref lastRangeEnd, line);
                }
                else if (line == "#EXT-X-DISCONTINUITY")
                {
                    discontinuity = true;
                }
                else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    throw Error(MediaParseError.InvalidPlaylist, "A master playlist cannot be parsed as a media playlist.");
                }
                else if (line.StartsWith('#'))
                {
                    // version, sequence, program-date-time, daterange and unknown tags do not change the segment list
                }
                else
                {
                    if (pendingDuration is null)
                    {
                        throw Error(MediaParseError.InvalidPlaylist, "A segment URI has no EXTINF.");
                    }

                    if (segments.Count >= MediaLimits.MaxSegmentsPerStream)
                    {
                        throw Error(MediaParseError.TooManyItems, "The media playlist has too many segments.");
                    }

                    var uri = Resolve(playlistUri, line, line);
                    segments.Add(new MediaSegment(
                        segments.Count,
                        uri,
                        pendingDuration.Value,
                        pendingRange,
                        currentKey,
                        discontinuity,
                        pendingTitle,
                        mediaSequence + (long)segments.Count));
                    duration += pendingDuration.Value;
                    lastRangeEnd = pendingRange is { } range ? range.EndInclusive + 1 : null;
                    pendingDuration = null;
                    pendingTitle = null;
                    pendingRange = null;
                    discontinuity = false;
                }
            }

            if (pendingDuration is not null)
            {
                throw Error(MediaParseError.InvalidPlaylist, "The playlist ends with an EXTINF and no segment URI.");
            }

            return new HlsMediaPlaylist(
                playlistUri,
                targetDuration,
                mediaSequence,
                duration,
                !endList,
                initialization,
                segments,
                firstKey);
        }
        catch (MediaParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MediaParseException(MediaParseError.InvalidPlaylist, "The HLS media playlist is invalid.", ex);
        }
    }

    private static HlsVariant ToVariant(Dictionary<string, string> attributes, Uri playlistUri, string line)
    {
        var bandwidth = RequiredPositiveInt(attributes, "BANDWIDTH", line);
        int? width = null;
        int? height = null;
        if (attributes.TryGetValue("RESOLUTION", out var resolution))
        {
            var x = resolution.IndexOf('x', StringComparison.OrdinalIgnoreCase);
            if (x <= 0 || !int.TryParse(resolution[..x], CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(resolution[(x + 1)..], CultureInfo.InvariantCulture, out var h) || w <= 0 || h <= 0)
            {
                throw Error(MediaParseError.InvalidAttribute, "RESOLUTION must be WIDTHxHEIGHT.");
            }

            width = w;
            height = h;
        }

        return new HlsVariant(
            playlistUri,
            bandwidth,
            OptionalInt(attributes, "AVERAGE-BANDWIDTH", line),
            width,
            height,
            attributes.GetValueOrDefault("CODECS"),
            attributes.GetValueOrDefault("AUDIO"),
            attributes.GetValueOrDefault("SUBTITLES"),
            OptionalDouble(attributes, "FRAME-RATE", line),
            attributes.GetValueOrDefault("VIDEO-RANGE"));
    }

    private static HlsRendition? ToRendition(Dictionary<string, string> attributes, Uri manifestUri, string line)
    {
        var typeText = attributes.GetValueOrDefault("TYPE")
            ?? throw Error(MediaParseError.InvalidAttribute, "EXT-X-MEDIA needs TYPE.");
        var type = typeText switch
        {
            "AUDIO" => HlsRenditionType.Audio,
            "SUBTITLES" => HlsRenditionType.Subtitles,
            _ => (HlsRenditionType?)null,
        };
        if (type is null || !attributes.TryGetValue("URI", out var uriText))
        {
            return null;
        }

        var group = attributes.GetValueOrDefault("GROUP-ID")
            ?? throw Error(MediaParseError.InvalidAttribute, "EXT-X-MEDIA needs GROUP-ID.");
        var name = attributes.GetValueOrDefault("NAME") ?? group;
        return new HlsRendition(
            type.Value,
            group,
            name,
            attributes.GetValueOrDefault("LANGUAGE"),
            Yes(attributes.GetValueOrDefault("DEFAULT")),
            Yes(attributes.GetValueOrDefault("AUTOSELECT")),
            Yes(attributes.GetValueOrDefault("FORCED")),
            Resolve(manifestUri, uriText, line),
            attributes.GetValueOrDefault("CHANNELS"),
            attributes.GetValueOrDefault("CHARACTERISTICS"));
    }

    private static MediaKey ToKey(Dictionary<string, string> attributes, Uri baseUri, string line)
    {
        var method = attributes.GetValueOrDefault("METHOD")
            ?? throw Error(MediaParseError.InvalidAttribute, "EXT-X-KEY needs METHOD.");
        if (string.IsNullOrWhiteSpace(method))
        {
            throw Error(MediaParseError.InvalidAttribute, "EXT-X-KEY METHOD cannot be empty.");
        }

        if (!attributes.TryGetValue("URI", out var uriText))
        {
            return new MediaKey(method, null, attributes.GetValueOrDefault("KEYFORMAT"), attributes.GetValueOrDefault("KEYFORMATVERSIONS"), attributes.GetValueOrDefault("IV"));
        }

        return new MediaKey(
            method,
            Resolve(baseUri, uriText, line, requireHttp: false),
            attributes.GetValueOrDefault("KEYFORMAT"),
            attributes.GetValueOrDefault("KEYFORMATVERSIONS"),
            attributes.GetValueOrDefault("IV"));
    }

    private static MediaInitSegment ToInit(Dictionary<string, string> attributes, Uri baseUri, string line)
    {
        if (!attributes.TryGetValue("URI", out var uriText))
        {
            throw Error(MediaParseError.InvalidAttribute, "EXT-X-MAP needs URI.");
        }

        MediaByteRange? range = null;
        if (attributes.TryGetValue("BYTERANGE", out var rangeText))
        {
            long? end = null;
            range = ParseByteRange(rangeText, ref end, line);
        }

        return new MediaInitSegment(Resolve(baseUri, uriText, line), range);
    }

    private static MediaByteRange ParseByteRange(string text, ref long? previousEnd, string line)
    {
        var at = text.IndexOf('@', StringComparison.Ordinal);
        var lengthText = at >= 0 ? text[..at] : text;
        if (!long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            throw Error(MediaParseError.InvalidAttribute, "BYTERANGE length must be positive.");
        }

        long offset;
        if (at >= 0)
        {
            if (!long.TryParse(text[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                throw Error(MediaParseError.InvalidAttribute, "BYTERANGE offset must be non-negative.");
            }
        }
        else if (previousEnd is { } end)
        {
            offset = end;
        }
        else
        {
            throw Error(MediaParseError.InvalidAttribute, "A BYTERANGE without an offset needs a previous range.");
        }

        return new MediaByteRange(offset, length);
    }

    private static Uri NextUri(List<string> lines, ref int index, Uri baseUri)
    {
        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                throw Error(MediaParseError.InvalidPlaylist, "EXT-X-STREAM-INF must be followed by a URI line.");
            }

            index = i;
            return Resolve(baseUri, line, line);
        }

        throw Error(MediaParseError.InvalidPlaylist, "EXT-X-STREAM-INF has no URI.");
    }

    private static List<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw Error(MediaParseError.Empty, "The manifest is empty.");
        }

        if (text.Length > MediaLimits.MaxManifestBytes || Encoding.UTF8.GetByteCount(text) > MediaLimits.MaxManifestBytes)
        {
            throw Error(MediaParseError.TooLarge, "The manifest exceeds the 10 MB cap.");
        }

        var lines = new List<string>();
        var first = true;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimEnd('\r');
            if (!first && line.Length == 0)
            {
                continue; // RFC 8216: blank lines are ignored
            }

            if (first)
            {
                line = line.TrimStart('\uFEFF');
                first = false;
                if (!string.Equals(line, "#EXTM3U", StringComparison.Ordinal))
                {
                    throw Error(MediaParseError.MissingHeader, "The playlist must start with #EXTM3U.");
                }
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            throw Error(MediaParseError.Empty, "The manifest is empty.");
        }

        return lines;
    }

    private static Dictionary<string, string> Attributes(string text, string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && (text[at] == ',' || char.IsWhiteSpace(text[at])))
            {
                at++;
            }

            if (at == text.Length)
            {
                break;
            }

            var nameStart = at;
            while (at < text.Length && text[at] != '=' && text[at] != ',')
            {
                at++;
            }

            if (at == text.Length || text[at] != '=')
            {
                throw Error(MediaParseError.InvalidAttribute, "An attribute list is malformed.");
            }

            var name = text[nameStart..at].Trim();
            if (name.Length == 0)
            {
                throw Error(MediaParseError.InvalidAttribute, "An attribute name is empty.");
            }

            at++;
            string value;
            if (at < text.Length && text[at] == '"')
            {
                at++;
                var start = at;
                while (at < text.Length && text[at] != '"')
                {
                    at++;
                }

                if (at == text.Length)
                {
                    throw Error(MediaParseError.InvalidAttribute, "An attribute quote is not closed.");
                }

                value = text[start..at];
                at++;
            }
            else
            {
                var start = at;
                while (at < text.Length && text[at] != ',')
                {
                    at++;
                }

                value = text[start..at].Trim();
            }

            result[name] = value;
        }

        return result;
    }

    private static Uri Resolve(Uri baseUri, string value, string line, bool requireHttp = true)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(baseUri, value.Trim(), out var uri) || !uri.IsAbsoluteUri)
        {
            throw Error(MediaParseError.InvalidUri, "A playlist URI is invalid.");
        }

        if (requireHttp && uri.Scheme is not ("http" or "https"))
        {
            throw Error(MediaParseError.InvalidUri, "Media URIs must be HTTP or HTTPS.");
        }

        return uri;
    }

    private static int RequiredPositiveInt(Dictionary<string, string> attributes, string name, string line)
    {
        if (!attributes.TryGetValue(name, out var text)
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a positive integer.");
        }

        return value;
    }

    private static int? OptionalInt(Dictionary<string, string> attributes, string name, string line)
    {
        if (!attributes.TryGetValue(name, out var text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a non-negative integer.");
        }

        return value;
    }

    private static double? OptionalDouble(Dictionary<string, string> attributes, string name, string line)
    {
        if (!attributes.TryGetValue(name, out var text))
        {
            return null;
        }

        return NonNegativeDouble(text, line);
    }

    private static int NonNegativeInt(string text, string line)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw Error(MediaParseError.InvalidNumber, "Expected a non-negative integer.");
        }

        return value;
    }

    private static double PositiveDouble(string text, string line)
    {
        var value = NonNegativeDouble(text, line);
        if (value <= 0)
        {
            throw Error(MediaParseError.InvalidNumber, "Expected a positive number.");
        }

        return value;
    }

    private static double NonNegativeDouble(string text, string line)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0)
        {
            throw Error(MediaParseError.InvalidNumber, "Expected a finite non-negative number.");
        }

        return value;
    }

    private static bool Yes(string? text) => string.Equals(text, "YES", StringComparison.OrdinalIgnoreCase);

    private static MediaParseException Error(MediaParseError error, string message) => new(error, message);
}
