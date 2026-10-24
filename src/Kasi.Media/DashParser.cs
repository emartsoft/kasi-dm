using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kasi.Media;

/// <summary>
/// Hand-written MPEG-DASH MPD parser. It resolves SegmentTemplate, SegmentList and SegmentBase into concrete segment URLs,
/// forbids DTD/XXE, and caps every collection before allocating. No manifest URL is fetched here.
/// </summary>
public static partial class DashParser
{
    public static MediaPresentation Parse(string? xml, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        try
        {
            if (xml is null || xml.Length == 0)
            {
                throw Error(MediaParseError.Empty, "The MPD is empty.");
            }

            if (xml.Length > MediaLimits.MaxManifestBytes || System.Text.Encoding.UTF8.GetByteCount(xml) > MediaLimits.MaxManifestBytes)
            {
                throw Error(MediaParseError.TooLarge, "The MPD exceeds the 10 MB cap.");
            }

            if (!manifestUri.IsAbsoluteUri || manifestUri.Scheme is not ("http" or "https"))
            {
                throw Error(MediaParseError.InvalidUri, "The MPD URI must be absolute HTTP or HTTPS.");
            }

            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MediaLimits.MaxManifestBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var mpd = document.Root;
            if (mpd is null || mpd.Name.LocalName != "MPD")
            {
                throw Error(MediaParseError.InvalidXml, "The root element must be MPD.");
            }

            var isLive = string.Equals(Attribute(mpd, "type"), "dynamic", StringComparison.OrdinalIgnoreCase);
            _ = ResolveBaseChain([mpd], manifestUri);
            var title = Child(mpd, "ProgramInformation") is { } information
                ? Child(information, "Title")?.Value.Trim()
                : null;
            var periods = Children(mpd, "Period").ToArray();
            if (periods.Length > MediaLimits.MaxPeriods)
            {
                throw Error(MediaParseError.TooManyItems, "The MPD has too many periods.");
            }

            if (periods.Length > 1)
            {
                // Each period would list its representations again as separate qualities; stitching periods is not in M3.
                throw Error(MediaParseError.Unsupported, "Multi-period MPDs are not supported.");
            }

            var declaredDuration = ParseDuration(Attribute(mpd, "mediaPresentationDuration"));
            var totalDuration = declaredDuration ?? periods
                .Select(period => ParseDuration(Attribute(period, "duration")))
                .Where(value => value is > 0)
                .DefaultIfEmpty(0)
                .Sum() ?? 0;

            var video = new List<MediaTrack>();
            var audio = new List<MediaTrack>();
            var subtitles = new List<MediaTrack>();

            for (var periodIndex = 0; periodIndex < periods.Length; periodIndex++)
            {
                var period = periods[periodIndex];
                var periodDuration = ParseDuration(Attribute(period, "duration")) ?? totalDuration;
                var adaptations = Children(period, "AdaptationSet").ToArray();
                if (adaptations.Length > MediaLimits.MaxAdaptationSets)
                {
                    throw Error(MediaParseError.TooManyItems, "The period has too many adaptation sets.");
                }

                for (var adaptationIndex = 0; adaptationIndex < adaptations.Length; adaptationIndex++)
                {
                    var adaptation = adaptations[adaptationIndex];
                    var role = RoleOf(adaptation);
                    if (role is null)
                    {
                        continue;
                    }

                    var adaptationProtection = Children(adaptation, "ContentProtection")
                        .Select(element => Attribute(element, "schemeIdUri"))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .ToArray();
                    var representations = Children(adaptation, "Representation").ToArray();
                    for (var representationIndex = 0; representationIndex < representations.Length; representationIndex++)
                    {
                        var representation = representations[representationIndex];
                        var representationBase = ResolveBaseChain([mpd, period, adaptation, representation], manifestUri);
                        var representationProtection = Children(representation, "ContentProtection")
                            .Select(element => Attribute(element, "schemeIdUri"))
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .ToArray();
                        var id = Attribute(representation, "id") ?? $"{periodIndex}-{adaptationIndex}-{representationIndex}";
                        var bandwidth = ParsePositiveInt(Attribute(representation, "bandwidth") ?? Attribute(adaptation, "bandwidth"), "bandwidth") ?? 0;
                        var width = ParsePositiveInt(Attribute(representation, "width") ?? Attribute(adaptation, "width"), "width");
                        var height = ParsePositiveInt(Attribute(representation, "height") ?? Attribute(adaptation, "height"), "height");
                        var mime = Attribute(representation, "mimeType") ?? Attribute(adaptation, "mimeType");
                        var codecs = Attribute(representation, "codecs") ?? Attribute(adaptation, "codecs");
                        var language = Attribute(representation, "lang") ?? Attribute(adaptation, "lang");
                        var baseForRepresentation = representationBase;
                        var segments = BuildSegments(
                            representation,
                            adaptation,
                            period,
                            baseForRepresentation,
                            periodDuration,
                            id,
                            bandwidth);
                        var stream = new MediaTrack(
                            $"dash:{periodIndex}:{adaptationIndex}:{id}",
                            role.Value,
                            language,
                            codecs,
                            mime,
                            bandwidth,
                            width,
                            height,
                            periodDuration,
                            isLive,
                            segments.Initialization,
                            segments.Segments,
                            null,
                            [.. adaptationProtection.Concat(representationProtection).Distinct(StringComparer.Ordinal)]);
                        (role == MediaRole.Video ? video : role == MediaRole.Audio ? audio : subtitles).Add(stream);
                    }
                }
            }

            video.Sort((left, right) =>
            {
                var heightComparison = (right.Height ?? 0).CompareTo(left.Height ?? 0);
                return heightComparison != 0 ? heightComparison : (right.Bandwidth ?? 0).CompareTo(left.Bandwidth ?? 0);
            });
            audio.Sort((left, right) => (right.Bandwidth ?? 0).CompareTo(left.Bandwidth ?? 0));
            if (video.Concat(audio).Concat(subtitles).Sum(stream => stream.Segments.Count) > MediaLimits.MaxSegmentsTotal)
            {
                throw Error(MediaParseError.TooManyItems, "The MPD exceeds the total segment cap.");
            }

            var variants = video.Select(stream => new MediaVariant(stream, BestAudio(stream, audio), null)).ToArray();
            var duration = totalDuration > 0 ? totalDuration : video.Select(s => s.DurationSeconds).DefaultIfEmpty(0).Max();
            return new MediaPresentation(
                string.IsNullOrWhiteSpace(title) ? null : title,
                duration,
                isLive,
                variants,
                [.. audio.DistinctBy(stream => stream.Id)],
                [.. subtitles.DistinctBy(stream => stream.Id)]);
        }
        catch (MediaParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MediaParseException(MediaParseError.InvalidXml, "The MPD is invalid.", ex);
        }
    }

    private static MediaTrack? BestAudio(MediaTrack video, List<MediaTrack> audio)
    {
        if (audio.Count == 0)
        {
            return null;
        }

        var sameLanguage = audio.Where(a => string.Equals(a.Language, video.Language, StringComparison.OrdinalIgnoreCase)).ToArray();
        return (sameLanguage.Length > 0 ? sameLanguage : [.. audio])[0];
    }

    private static MediaRole? RoleOf(XElement adaptation)
    {
        var contentType = Attribute(adaptation, "contentType");
        var mime = Attribute(adaptation, "mimeType");
        return contentType switch
        {
            "video" => MediaRole.Video,
            "audio" => MediaRole.Audio,
            "text" => MediaRole.Subtitle,
            _ when mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true => MediaRole.Video,
            _ when mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true => MediaRole.Audio,
            _ when mime?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true => MediaRole.Subtitle,
            _ => null,
        };
    }

    private static (MediaInitSegment? Initialization, IReadOnlyList<MediaSegment> Segments) BuildSegments(
        XElement representation,
        XElement adaptation,
        XElement period,
        Uri baseUri,
        double periodDuration,
        string representationId,
        int bandwidth)
    {
        var template = MergeTemplate(representation, adaptation, period);
        if (template is not null)
        {
            return BuildTemplate(template, baseUri, periodDuration, representationId, bandwidth);
        }

        var list = Child(representation, "SegmentList") ?? Child(adaptation, "SegmentList") ?? Child(period, "SegmentList");
        if (list is not null)
        {
            return BuildList(list, baseUri, periodDuration);
        }

        var baseSegment = Child(representation, "SegmentBase") ?? Child(adaptation, "SegmentBase") ?? Child(period, "SegmentBase");
        if (baseSegment is not null)
        {
            return BuildBase(baseSegment, baseUri, periodDuration);
        }

        return (null, [new MediaSegment(0, baseUri, periodDuration)]);
    }

    private static XElement? MergeTemplate(XElement representation, XElement adaptation, XElement period)
    {
        var elements = new[] { Child(period, "SegmentTemplate"), Child(adaptation, "SegmentTemplate"), Child(representation, "SegmentTemplate") }
            .Where(element => element is not null)
            .ToArray();
        if (elements.Length == 0)
        {
            return null;
        }

        var merged = new XElement("SegmentTemplate");
        foreach (var element in elements)
        {
            foreach (var attribute in element!.Attributes())
            {
                merged.SetAttributeValue(attribute.Name, attribute.Value);
            }
        }

        var timeline = elements.Select(element => Child(element!, "SegmentTimeline")).LastOrDefault(element => element is not null);
        if (timeline is not null)
        {
            merged.Add(new XElement(timeline.Name, timeline.Attributes(), timeline.Elements()));
        }

        return merged;
    }

    private static (MediaInitSegment? Initialization, IReadOnlyList<MediaSegment> Segments) BuildTemplate(
        XElement template,
        Uri baseUri,
        double periodDuration,
        string representationId,
        int bandwidth)
    {
        var timescale = ParsePositiveInt(Attribute(template, "timescale"), "timescale") ?? 1;
        var startNumber = ParseNonNegativeInt(Attribute(template, "startNumber"), "startNumber") ?? 1;
        var media = Attribute(template, "media");
        if (string.IsNullOrWhiteSpace(media))
        {
            throw Error(MediaParseError.InvalidAttribute, "SegmentTemplate needs a media template.");
        }

        var initialization = InitializationFromTemplate(template, baseUri, representationId, bandwidth);
        var timeline = Child(template, "SegmentTimeline");
        var segments = new List<MediaSegment>();
        if (timeline is not null)
        {
            var entries = Children(timeline, "S").ToArray();
            if (entries.Length > MediaLimits.MaxTimelineEntries)
            {
                throw Error(MediaParseError.TooManyItems, "SegmentTimeline has too many entries.");
            }

            long? previousEnd = null;
            var number = startNumber;
            foreach (var entry in entries)
            {
                var duration = ParsePositiveLong(Attribute(entry, "d"), "d")
                    ?? throw Error(MediaParseError.InvalidAttribute, "SegmentTimeline S needs d.");
                var start = ParseNonNegativeLong(Attribute(entry, "t"), "t") ?? previousEnd
                    ?? throw Error(MediaParseError.InvalidAttribute, "The first SegmentTimeline S needs t.");
                if (Attribute(entry, "r")?.Trim() == "-1")
                {
                    throw Error(MediaParseError.Unsupported, "Open-ended SegmentTimeline repeats (r=-1) are not supported.");
                }

                var repeat = ParseNonNegativeInt(Attribute(entry, "r"), "r") ?? 0;

                if (repeat + 1 > MediaLimits.MaxSegmentsPerStream - segments.Count)
                {
                    throw Error(MediaParseError.TooManyItems, "SegmentTimeline expansion exceeds the segment cap.");
                }

                for (var repeatIndex = 0; repeatIndex <= repeat; repeatIndex++)
                {
                    var time = checked(start + (duration * repeatIndex));
                    segments.Add(CreateTemplateSegment(
                        segments.Count,
                        media,
                        baseUri,
                        representationId,
                        bandwidth,
                        number++,
                        time,
                        duration / (double)timescale,
                        initialization));
                }

                previousEnd = checked(start + (duration * (repeat + 1L)));
            }
        }
        else
        {
            var duration = ParsePositiveLong(Attribute(template, "duration"), "duration")
                ?? throw Error(MediaParseError.InvalidAttribute, "SegmentTemplate needs duration or SegmentTimeline.");
            var segmentDuration = duration / (double)timescale;
            var count = (int)Math.Ceiling(periodDuration / segmentDuration);
            if (count < 0 || count > MediaLimits.MaxSegmentsPerStream)
            {
                throw Error(MediaParseError.TooManyItems, "SegmentTemplate expansion exceeds the segment cap.");
            }

            for (var index = 0; index < count; index++)
            {
                segments.Add(CreateTemplateSegment(
                    segments.Count,
                    media,
                    baseUri,
                    representationId,
                    bandwidth,
                    startNumber + index,
                    checked((long)index * duration),
                    segmentDuration,
                    initialization));
            }
        }

        if (segments.Count == 0)
        {
            throw Error(MediaParseError.InvalidPlaylist, "SegmentTemplate produced no segments.");
        }

        return (initialization, segments);
    }

    private static MediaSegment CreateTemplateSegment(
        int index,
        string media,
        Uri baseUri,
        string representationId,
        int bandwidth,
        long number,
        long time,
        double duration,
        MediaInitSegment? initialization)
    {
        var uri = ExpandTemplate(media, representationId, bandwidth, number, time);
        return new MediaSegment(index, Resolve(baseUri, uri), duration, Key: null);
    }

    private static MediaInitSegment? InitializationFromTemplate(XElement template, Uri baseUri, string representationId, int bandwidth)
    {
        var initialization = Attribute(template, "initialization");
        return string.IsNullOrWhiteSpace(initialization)
            ? null
            : new MediaInitSegment(Resolve(baseUri, ExpandTemplate(initialization, representationId, bandwidth, 0, 0)));
    }

    private static (MediaInitSegment? Initialization, IReadOnlyList<MediaSegment> Segments) BuildList(
        XElement list,
        Uri baseUri,
        double periodDuration)
    {
        var timescale = ParsePositiveInt(Attribute(list, "timescale"), "timescale") ?? 1;
        var durationTicks = ParsePositiveLong(Attribute(list, "duration"), "duration");
        var initialization = InitializationFromSource(Child(list, "Initialization"), baseUri);
        var urls = Children(list, "SegmentURL").ToArray();
        if (urls.Length == 0)
        {
            throw Error(MediaParseError.InvalidPlaylist, "SegmentList has no SegmentURL.");
        }

        if (urls.Length > MediaLimits.MaxSegmentsPerStream)
        {
            throw Error(MediaParseError.TooManyItems, "SegmentList has too many segments.");
        }

        var duration = durationTicks is { } ticks ? ticks / (double)timescale : periodDuration / urls.Length;
        var segments = new List<MediaSegment>(urls.Length);
        for (var i = 0; i < urls.Length; i++)
        {
            var uriText = Attribute(urls[i], "media");
            if (string.IsNullOrWhiteSpace(uriText))
            {
                throw Error(MediaParseError.InvalidAttribute, "SegmentURL needs media.");
            }

            var range = ParseRange(Attribute(urls[i], "mediaRange"));
            segments.Add(new MediaSegment(i, Resolve(baseUri, uriText), duration, range));
        }

        return (initialization, segments);
    }

    private static (MediaInitSegment? Initialization, IReadOnlyList<MediaSegment> Segments) BuildBase(
        XElement segmentBase,
        Uri baseUri,
        double periodDuration)
    {
        var initialization = InitializationFromSource(Child(segmentBase, "Initialization"), baseUri);
        if (initialization is not null && initialization.Uri == baseUri)
        {
            // The init range is the start of the same file the single segment fetches whole; keeping it would duplicate it.
            initialization = null;
        }

        // Without parsing sidx, an on-demand SegmentBase representation is one whole-resource segment.
        return (initialization, [new MediaSegment(0, baseUri, periodDuration)]);
    }

    private static MediaInitSegment? InitializationFromSource(XElement? initialization, Uri baseUri)
    {
        if (initialization is null)
        {
            return null;
        }

        var source = Attribute(initialization, "sourceURL");
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return new MediaInitSegment(Resolve(baseUri, source), ParseRange(Attribute(initialization, "range")));
    }

    private static string ExpandTemplate(string template, string representationId, int bandwidth, long number, long time)
    {
        var result = template
            .Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", bandwidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        result = NumberPlaceholder().Replace(result, match =>
        {
            var variable = match.Groups[1].Value;
            var value = variable switch
            {
                "Number" => number,
                "Time" => time,
                _ => throw Error(MediaParseError.Unsupported, $"Unknown SegmentTemplate variable {variable}."),
            };
            return match.Groups[2].Success
                ? value.ToString($"D{int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)}", CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
        });
        if (result.Contains('$', StringComparison.Ordinal))
        {
            throw Error(MediaParseError.Unsupported, "A SegmentTemplate variable is malformed.");
        }

        return result;
    }

    private static Uri ResolveBaseChain(IReadOnlyList<XElement> chain, Uri manifestUri)
    {
        var current = manifestUri;
        var depth = 0;
        foreach (var element in chain)
        {
            if (BaseUrl(element, current) is not { } next)
            {
                continue;
            }

            if (++depth > MediaLimits.MaxBaseUrlDepth)
            {
                throw Error(MediaParseError.TooManyItems, "Nested BaseURL depth exceeds the cap.");
            }

            current = next;
        }

        return current;
    }

    private static Uri? BaseUrl(XElement element, Uri current)
    {
        var baseUrl = Child(element, "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(baseUrl) ? null : Resolve(current, baseUrl);
    }

    private static Uri Resolve(Uri baseUri, string value)
    {
        if (!Uri.TryCreate(baseUri, value.Trim(), out var uri) || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw Error(MediaParseError.InvalidUri, "A media URI must be absolute HTTP or HTTPS.");
        }

        return uri;
    }

    private static MediaByteRange? ParseRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0
            || !long.TryParse(text[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(text[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
            || start < 0 || end < start)
        {
            throw Error(MediaParseError.InvalidAttribute, "A byte range must be start-end.");
        }

        return new MediaByteRange(start, end - start + 1);
    }

    private static double? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = IsoDuration().Match(text.Trim());
        if (!match.Success)
        {
            throw Error(MediaParseError.InvalidNumber, "An ISO 8601 duration is invalid.");
        }

        var days = Number(match.Groups[1].Value);
        var hours = Number(match.Groups[2].Value);
        var minutes = Number(match.Groups[3].Value);
        var seconds = Number(match.Groups[4].Value);
        return (days * 86400) + (hours * 3600) + (minutes * 60) + seconds;
    }

    private static double Number(string text) =>
        string.IsNullOrEmpty(text) ? 0 : double.Parse(text, CultureInfo.InvariantCulture);

    private static int? ParsePositiveInt(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a positive integer.");
        }

        return value;
    }

    private static long? ParsePositiveLong(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a positive integer.");
        }

        return value;
    }

    private static long? ParseNonNegativeLong(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a non-negative integer.");
        }

        return value;
    }

    private static int? ParseNonNegativeInt(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw Error(MediaParseError.InvalidAttribute, $"{name} must be a non-negative integer.");
        }

        return value;
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(child => child.Name.LocalName == localName);

    private static MediaParseException Error(MediaParseError error, string message) => new(error, message);

    [GeneratedRegex(@"^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDuration();

    [GeneratedRegex(@"\$(Number|Time)(?:%0(\d+)d)?\$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPlaceholder();
}
