using System.Globalization;
using System.Text;

namespace Kasi.TestServer.Endpoints;

/// <summary><c>/hls/{kind}/…</c> and <c>/dash/{kind}/…</c>: generated manifests and segments (TPL 3.1).</summary>
internal static class StreamingEndpoints
{
    private const string HlsContentType = "application/vnd.apple.mpegurl";
    private const string DashContentType = "application/dash+xml";

    private const string FairPlay = "com.apple.streamingkeydelivery";
    private const string Widevine = "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed";
    private const string PlayReady = "urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95";
    private const string FairPlayUuid = "urn:uuid:94ce86fb-07ff-4f43-adb8-93d2fa968ca2";
    private const string ClearKey = "urn:uuid:e2719d58-a985-b3c9-781a-b030af78d30e";

    private static readonly Dictionary<string, string?> _hlsKeyLines = new()
    {
        ["clear"] = null,
        ["aes128"] = "METHOD=AES-128,URI=\"../key.bin\"",
        ["sample-aes"] = $"METHOD=SAMPLE-AES,URI=\"skd://kasi-test\",KEYFORMAT=\"{FairPlay}\",KEYFORMATVERSIONS=\"1\"",
        ["sample-aes-ctr"] = $"METHOD=SAMPLE-AES-CTR,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"{Widevine}\",KEYFORMATVERSIONS=\"1\"",
        ["keyformat-fairplay"] = $"METHOD=AES-128,URI=\"skd://kasi-test\",KEYFORMAT=\"{FairPlay}\",KEYFORMATVERSIONS=\"1\"",
        ["keyformat-widevine"] = $"METHOD=AES-128,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"{Widevine}\"",
        ["keyformat-playready"] = "METHOD=AES-128,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"com.microsoft.playready\"",
    };

    private static readonly Dictionary<string, string?> _dashProtection = new()
    {
        ["clear"] = null,
        ["segment-list"] = null,
        ["widevine"] = $"<ContentProtection schemeIdUri=\"{Widevine}\" value=\"Widevine\"/>",
        ["playready"] = $"<ContentProtection schemeIdUri=\"{PlayReady}\" value=\"MSPR 2.0\"/>",
        ["fairplay"] = $"<ContentProtection schemeIdUri=\"{FairPlayUuid}\" value=\"FairPlay\"/>",
        ["clearkey"] = $"<ContentProtection schemeIdUri=\"{ClearKey}\" value=\"ClearKey1.0\"/>",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var liveTime = app.ServiceProvider.GetRequiredService<TimeProvider>();
        var liveStart = liveTime.GetUtcNow();
        int LiveCount() => Math.Min(150, 1 + (int)Math.Max(0,
            Math.Floor((liveTime.GetUtcNow() - liveStart).TotalSeconds / StreamFixtures.SegmentSeconds)));

        app.MapGet("/hls/live/index.m3u8", () => Results.Text(LivePlaylist(LiveCount()), HlsContentType));
        app.MapGet("/hls/live/seg{index:int}.ts", (int index) =>
            index >= Math.Max(0, LiveCount() - 6) && index < LiveCount()
                ? Results.Bytes(MuxableFixtures.LiveSegment(index), "video/mp2t")
                : Results.NotFound());

        app.MapGet("/hls/muxable/master.m3u8", () => Results.Text(MuxableFixtures.HlsMaster(), HlsContentType));
        app.MapGet("/hls/muxable-vtt/master.m3u8", () => Results.Text(SubtitledMaster(), HlsContentType));
        app.MapGet("/hls/muxable-vtt/captions/index.m3u8", () => Results.Text(SubtitledPlaylist(), HlsContentType));
        app.MapGet("/hls/muxable-vtt/captions/cue-{index:int}.vtt", (int index) =>
            index is >= 0 and < MuxableFixtures.SegmentCount
                ? Results.Text($"WEBVTT\n\n00:00:{index * 4:00}.000 --> 00:00:{(index + 1) * 4:00}.000\nCaption {index}\n", "text/vtt")
                : Results.NotFound());
        app.MapGet("/hls/muxable/index.m3u8", () => Results.Text(MuxableFixtures.HlsMedia(encrypted: false), HlsContentType));
        app.MapGet("/hls/muxable/segment-{index:int}.ts", (int index) =>
            index is >= 0 and < MuxableFixtures.SegmentCount
                ? Results.Bytes(MuxableFixtures.HlsSegment(index), "video/mp2t")
                : Results.NotFound());

        app.MapGet("/hls/muxable-aes128/master.m3u8", () => Results.Text(MuxableFixtures.HlsMaster(), HlsContentType));
        app.MapGet("/hls/muxable-aes128/index.m3u8", () => Results.Text(MuxableFixtures.HlsMedia(encrypted: true), HlsContentType));
        app.MapGet("/hls/muxable-aes128/segment-{index:int}.ts", (int index) =>
            index is >= 0 and < MuxableFixtures.SegmentCount
                ? Results.Bytes(MuxableFixtures.HlsEncryptedSegment(index), "video/mp2t")
                : Results.NotFound());
        app.MapGet("/hls/muxable-aes128/key.bin", () => Results.Bytes(MuxableFixtures.KeyBytes(), "application/octet-stream"));

        app.MapGet("/dash/muxable/manifest.mpd", () => Results.Text(MuxableFixtures.DashManifestText(), DashContentType));
        app.MapGet("/dash/muxable/init.mp4", () => Results.Bytes(MuxableFixtures.DashInit(), "video/mp4"));
        app.MapGet("/dash/muxable/seg-{number:int}.m4s", (int number) =>
            number is >= 1 and <= MuxableFixtures.SegmentCount
                ? Results.Bytes(MuxableFixtures.DashSegment(number), "video/mp4")
                : Results.NotFound());

        app.MapGet("/media/direct/{size:long}", (long size) =>
            size is >= 1 and <= 8_000_000
                ? Results.Bytes(GeneratedContent.Bytes("media-direct", size), "video/mp4")
                : Results.NotFound());

        app.MapGet("/hls/{kind}/master.m3u8", (string kind) =>
            _hlsKeyLines.ContainsKey(kind) ? Results.Text(HlsMaster(), HlsContentType) : Results.NotFound());

        app.MapGet("/hls/{kind}/{rendition}/index.m3u8", (string kind, string rendition) =>
            _hlsKeyLines.TryGetValue(kind, out var key) && StreamFixtures.HlsRenditions.ContainsKey(rendition)
                ? Results.Text(HlsMedia(key), HlsContentType)
                : Results.NotFound());

        app.MapGet("/hls/{kind}/{rendition}/seg{index:int}.ts", (string kind, string rendition, int index) =>
        {
            if (!_hlsKeyLines.ContainsKey(kind) || !StreamFixtures.HlsRenditions.ContainsKey(rendition) || index is < 0 or >= StreamFixtures.SegmentCount)
            {
                return Results.NotFound();
            }

            var bytes = kind == "aes128" ? StreamFixtures.HlsEncryptedSegment(rendition, index) : StreamFixtures.HlsSegment(rendition, index);
            return Results.Bytes(bytes, "video/mp2t");
        });

        app.MapGet("/hls/aes128/key.bin", () => Results.Bytes(StreamFixtures.HlsKey, "application/octet-stream"));

        app.MapGet("/dash/{kind}/manifest.mpd", (string kind) =>
            _dashProtection.TryGetValue(kind, out var protection) ? Results.Text(DashManifest(kind, protection), DashContentType) : Results.NotFound());

        app.MapGet("/dash/{kind}/{representation}/init.mp4", (string kind, string representation) =>
            _dashProtection.ContainsKey(kind) && StreamFixtures.DashRepresentations.Contains(representation)
                ? Results.Bytes(StreamFixtures.DashInit(representation), "video/mp4")
                : Results.NotFound());

        app.MapGet("/dash/{kind}/{representation}/seg-{number:int}.m4s", (string kind, string representation, int number) =>
            _dashProtection.ContainsKey(kind) && StreamFixtures.DashRepresentations.Contains(representation) && number is >= 1 and <= StreamFixtures.SegmentCount
                ? Results.Bytes(StreamFixtures.DashSegment(representation, number), "video/mp4")
                : Results.NotFound());
    }

    private static string HlsMaster() => string.Join('\n',
        "#EXTM3U",
        "#EXT-X-VERSION:3",
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,URI=\"audio/index.m3u8\"",
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"",
        "360p/index.m3u8",
        "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\",AUDIO=\"aud\"",
        "720p/index.m3u8",
        string.Empty);

    private static string SubtitledMaster() => string.Join('\n',
        "#EXTM3U",
        "#EXT-X-VERSION:3",
        "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,URI=\"captions/index.m3u8\"",
        "#EXT-X-STREAM-INF:BANDWIDTH=208000,RESOLUTION=160x90,CODECS=\"avc1.42c00c,mp4a.40.2\",SUBTITLES=\"subs\"",
        "../muxable/index.m3u8",
        string.Empty);

    private static string SubtitledPlaylist() => string.Join('\n',
        ["#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:4", "#EXT-X-MEDIA-SEQUENCE:0",
            .. Enumerable.Range(0, MuxableFixtures.SegmentCount).SelectMany(index => new[] { "#EXTINF:4.000,", $"cue-{index}.vtt" }),
            "#EXT-X-ENDLIST", string.Empty]);

    private static string LivePlaylist(int count)
    {
        var first = Math.Max(0, count - 6);
        return string.Join('\n',
            ["#EXTM3U", "#EXT-X-VERSION:3", $"#EXT-X-TARGETDURATION:{StreamFixtures.SegmentSeconds}",
                $"#EXT-X-MEDIA-SEQUENCE:{first}",
                .. Enumerable.Range(first, count - first).SelectMany(index => new[] { "#EXTINF:4.000,", $"seg{index}.ts" }),
                .. (count == 150 ? new[] { "#EXT-X-ENDLIST" } : []), string.Empty]);
    }

    private static string HlsMedia(string? keyLine)
    {
        var text = new StringBuilder()
            .Append("#EXTM3U\n#EXT-X-VERSION:3\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{StreamFixtures.SegmentSeconds}\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (keyLine is not null)
        {
            text.Append("#EXT-X-KEY:").Append(keyLine).Append('\n');
        }

        for (var i = 0; i < StreamFixtures.SegmentCount; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"#EXTINF:{StreamFixtures.SegmentSeconds}.000,\nseg{i}.ts\n");
        }

        return text.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private static string DashManifest(string kind, string? protection)
    {
        var duration = StreamFixtures.SegmentCount * StreamFixtures.SegmentSeconds;
        var useList = kind == "segment-list";
        var template = useList
            ? string.Empty
            : $"""<SegmentTemplate timescale="1000" duration="{StreamFixtures.SegmentSeconds * 1000}" startNumber="1" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number$.m4s"/>""";

        string Representation(string id, string attributes) =>
            $"""<Representation id="{id}" {attributes}>{(useList ? SegmentList(id) : string.Empty)}</Representation>""";

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT{duration}S" minBufferTime="PT2S" profiles="urn:mpeg:dash:profile:isoff-on-demand:2011">
              <Period id="1" duration="PT{duration}S">
                <AdaptationSet id="1" contentType="video" mimeType="video/mp4" segmentAlignment="true">
                  {protection}
                  {template}
                  {Representation("video-360p", "bandwidth=\"800000\" width=\"640\" height=\"360\" codecs=\"avc1.4d401e\"")}
                  {Representation("video-720p", "bandwidth=\"2500000\" width=\"1280\" height=\"720\" codecs=\"avc1.4d401f\"")}
                </AdaptationSet>
                <AdaptationSet id="2" contentType="audio" mimeType="audio/mp4" lang="en">
                  {protection}
                  {template}
                  {Representation("audio-en", "bandwidth=\"128000\" codecs=\"mp4a.40.2\" audioSamplingRate=\"48000\"")}
                </AdaptationSet>
              </Period>
            </MPD>
            """;
    }

    private static string SegmentList(string representation)
    {
        var urls = string.Concat(Enumerable.Range(1, StreamFixtures.SegmentCount).Select(n => $"""<SegmentURL media="{representation}/seg-{n}.m4s"/>"""));
        return $"""<SegmentList timescale="1000" duration="{StreamFixtures.SegmentSeconds * 1000}"><Initialization sourceURL="{representation}/init.mp4"/>{urls}</SegmentList>""";
    }
}
