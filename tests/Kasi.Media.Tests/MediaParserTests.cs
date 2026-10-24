using FsCheck.Xunit;

namespace Kasi.Media.Tests;

[Trait("Requirement", "KASI-MED-002")]
[Trait("TC", "TC-MED-002")]
public sealed class MediaParserTests
{
    private static readonly Uri _manifest = new("https://media.example/dash/manifest.mpd");
    private static readonly Uri _master = new("https://media.example/hls/master.m3u8");

    [Fact]
    public void Hls_master_lists_variants_and_audio_and_pairs_the_default_audio()
    {
        var master = HlsParser.ParseMaster(Fixture("hls", "master.m3u8"), _master);

        master.Variants.Should().HaveCount(2);
        master.Variants.Should().ContainSingle(variant => variant.Height == 720 && variant.Bandwidth == 2_500_000);
        master.Renditions.Should().ContainSingle(rendition => rendition.Type == HlsRenditionType.Audio && rendition.Default);
        var audioMedia = HlsParser.ParseMedia(Fixture("hls", "audio.m3u8"), new Uri("https://media.example/hls/audio.m3u8"));
        var media720 = HlsParser.ParseMedia(Fixture("hls", "720p.m3u8"), new Uri("https://media.example/hls/720p.m3u8"));
        var media360 = HlsParser.ParseMedia(Fixture("hls", "360p.m3u8"), new Uri("https://media.example/hls/360p.m3u8"));

        var presentation = HlsPresentationBuilder.Build(master, uri => uri.AbsolutePath switch
        {
            "/hls/720p.m3u8" => media720,
            "/hls/360p.m3u8" => media360,
            "/hls/audio.m3u8" => audioMedia,
            _ => throw new InvalidOperationException(uri.AbsoluteUri),
        });

        presentation.Variants.Should().HaveCount(2);
        presentation.Variants[0].Height.Should().Be(720);
        presentation.Variants[0].Audio.Should().NotBeNull();
        presentation.Variants[0].Audio!.Language.Should().Be("en");
        presentation.Variants[0].EstimatedBytes.Should().Be(3_750_000);
        presentation.AudioOnly.Should().ContainSingle();
        presentation.SelectDefault(1080)!.Height.Should().Be(720);
        presentation.SelectDefault(480)!.Height.Should().Be(360);
    }

    [Fact]
    public void Hls_media_parses_map_key_byte_ranges_discontinuity_and_live_state()
    {
        var playlist = HlsParser.ParseMedia(Fixture("hls", "720p.m3u8"), new Uri("https://media.example/hls/720p.m3u8"));

        playlist.TargetDurationSeconds.Should().Be(4);
        playlist.DurationSeconds.Should().Be(12);
        playlist.IsLive.Should().BeFalse();
        playlist.Segments.Should().HaveCount(3);
        playlist.Initialization!.Range.Should().Be(new MediaByteRange(0, 100));
        playlist.Segments[1].Range.Should().Be(new MediaByteRange(200, 1000));
        playlist.Segments[1].Title.Should().Be("second");
        playlist.Segments[2].Discontinuity.Should().BeTrue();
        playlist.Segments[0].Key!.Method.Should().Be("AES-128");
        playlist.Segments[0].Key!.Uri.Should().Be(new Uri("https://media.example/hls/key.bin"));

        var live = HlsParser.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\na.ts\n", new Uri("https://media.example/live.m3u8"));
        live.IsLive.Should().BeTrue();
        live.DurationSeconds.Should().Be(4);
    }

    [Fact]
    public void Dash_segment_template_builds_variants_audio_pairing_and_init()
    {
        var presentation = DashParser.Parse(Fixture("dash", "template.mpd"), _manifest);

        presentation.DurationSeconds.Should().Be(24);
        presentation.IsLive.Should().BeFalse();
        presentation.Variants.Should().HaveCount(2);
        presentation.Variants[0].Height.Should().Be(720);
        presentation.Variants[0].Audio.Should().NotBeNull();
        presentation.Variants[0].Video.Segments.Should().HaveCount(6);
        presentation.Variants[0].Video.Segments[0].Uri
            .Should().Be(new Uri("https://media.example/dash/video-720p/seg-1.m4s"));
        presentation.Variants[0].Video.Initialization!.Uri
            .Should().Be(new Uri("https://media.example/dash/video-720p/init.mp4"));
        presentation.AudioOnly.Should().ContainSingle();
        presentation.Variants[0].EstimatedBytes.Should().Be(7_884_000);
    }

    [Fact]
    public void Dash_timeline_uses_time_placeholders_and_reports_live()
    {
        var presentation = DashParser.Parse(Fixture("dash", "timeline.mpd"), _manifest);

        presentation.IsLive.Should().BeTrue();
        var video = presentation.Variants[0].Video;
        video.Segments.Should().HaveCount(3);
        video.Segments.Select(segment => segment.Uri.AbsolutePath).Should().Equal(
            "/dash/seg-0.m4s",
            "/dash/seg-4000.m4s",
            "/dash/seg-8000.m4s");
        video.Initialization!.Uri.AbsolutePath.Should().Be("/dash/init.mp4");
    }

    [Fact]
    public void Dash_segment_list_builds_ranges_and_initialization()
    {
        var presentation = DashParser.Parse(Fixture("dash", "list.mpd"), _manifest);

        var video = presentation.Variants[0].Video;
        video.Segments.Should().HaveCount(3);
        video.Segments[0].Range.Should().Be(new MediaByteRange(100, 1000));
        video.Initialization!.Uri.AbsolutePath.Should().Be("/dash/video/init.mp4");
        video.Initialization.Range.Should().Be(new MediaByteRange(0, 100));
    }

    [Fact]
    public void Dash_segment_base_is_one_whole_resource_segment_with_an_init()
    {
        var presentation = DashParser.Parse(Fixture("dash", "base.mpd"), _manifest);

        var video = presentation.Variants[0].Video;
        video.Segments.Should().ContainSingle();
        video.Segments[0].Uri.AbsolutePath.Should().Be("/dash/video.mp4");
        video.Initialization!.Uri.AbsolutePath.Should().Be("/dash/video-init.mp4");
    }

    [Fact]
    public void Hostile_manifests_fail_fast_with_a_typed_error()
    {
        var noHeader = () => HlsParser.ParseMaster("#EXTM3U\n", _master);
        noHeader.Should().NotThrow();

        Action noHlsHeader = () => HlsParser.ParseMaster("not a playlist", _master);
        noHlsHeader.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.MissingHeader);

        Action dtd = () => DashParser.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><MPD>&e;</MPD>", _manifest);
        dtd.Should().Throw<MediaParseException>();

        var huge = new string('x', MediaLimits.MaxManifestBytes + 1);
        Action tooLarge = () => HlsParser.ParseMaster(huge, _master);
        tooLarge.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.TooLarge);

        var timeline = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="s-$Time$.m4s"><SegmentTimeline><S t="0" d="1" r="1000000"/></SegmentTimeline></SegmentTemplate>
                  <Representation id="v" bandwidth="1"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        Action tooMany = () => DashParser.Parse(timeline, _manifest);
        tooMany.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.TooManyItems);

        var multiBytes = new string('\u20AC', MediaLimits.MaxManifestBytes / 2);
        Action tooManyBytes = () => HlsParser.ParseMaster(multiBytes, _master);
        tooManyBytes.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.TooLarge);
    }

    [Fact]
    public void Hls_ignores_blank_lines_and_does_not_leak_uris_in_errors()
    {
        var media = HlsParser.ParseMedia("#EXTM3U\r\n#EXT-X-TARGETDURATION:4\r\n\r\n#EXTINF:4,\r\nseg0.ts\r\n\r\n#EXTINF:4,\r\nseg1.ts\r\n#EXT-X-ENDLIST\r\n\r\n\r\n", _master);
        media.Segments.Should().HaveCount(2);

        var master = HlsParser.ParseMaster("#EXTM3U\n\n#EXT-X-STREAM-INF:BANDWIDTH=100\n\nv.m3u8\n\n", _master);
        master.Variants.Should().ContainSingle();

        Action bad = () => HlsParser.ParseMaster("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nhttp://[bad?token=SECRET\n", _master);
        bad.Should().Throw<MediaParseException>().Which.Message.Should().NotContain("SECRET");
    }

    [Fact]
    public void Hls_protection_signals_include_later_segment_keys_and_session_keys()
    {
        var media = HlsParser.ParseMedia(
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\na.ts\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\nb.ts\n#EXT-X-ENDLIST\n",
            _master);
        var track = new MediaTrack("t", MediaRole.Video, null, null, null, 1, null, null, 8, false, null, media.Segments, media.Key);
        track.ProtectionSignals.Should().Contain("SAMPLE-AES").And.Contain("com.apple.streamingkeydelivery");

        var master = HlsParser.ParseMaster(
            "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXT-X-STREAM-INF:BANDWIDTH=100\nv.m3u8\n",
            _master);
        var clear = HlsParser.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", new Uri("https://media.example/hls/v.m3u8"));
        var presentation = HlsPresentationBuilder.Build(master, _ => clear);
        presentation.Variants[0].Video.ProtectionSignals.Should().Contain("SAMPLE-AES");
    }

    [Fact]
    public void Dash_accepts_start_number_zero_and_repeated_base_urls()
    {
        const string Mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <BaseURL>https://cdn.example/x/</BaseURL>
              <Period><BaseURL>https://cdn.example/x/</BaseURL>
                <AdaptationSet mimeType="video/mp4">
                  <SegmentTemplate media="s$Number$.m4s" duration="4" startNumber="0"/>
                  <Representation id="v" bandwidth="100"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var video = DashParser.Parse(Mpd, _manifest).Variants[0].Video;
        video.Segments.Select(s => s.Uri.AbsolutePath).Should().Equal("/x/s0.m4s", "/x/s1.m4s");
    }

    [Fact]
    public void Dash_refuses_open_ended_repeats_multiple_periods_and_overflow()
    {
        const string Open = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4"><SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="0" d="4" r="-1"/></SegmentTimeline></SegmentTemplate>
              <Representation id="v" bandwidth="100"/></AdaptationSet></Period></MPD>
            """;
        Action open = () => DashParser.Parse(Open, _manifest);
        open.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.Unsupported);

        const string Two = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S"><Period/><Period/></MPD>
            """;
        Action two = () => DashParser.Parse(Two, _manifest);
        two.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.Unsupported);

        const string Overflow = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4"><SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="9223372036854775000" d="9000000000000000000" r="5"/></SegmentTimeline></SegmentTemplate>
              <Representation id="v" bandwidth="100"/></AdaptationSet></Period></MPD>
            """;
        Action overflow = () => DashParser.Parse(Overflow, _manifest);
        overflow.Should().Throw<MediaParseException>();
    }

    [Property(MaxTest = 80, QuietOnSuccess = true)]
    public void Hls_parser_throws_only_MediaParseException(string text)
    {
        TryParse(() => HlsParser.ParseMaster(text, _master));
        TryParse(() => HlsParser.ParseMedia(text, _master));
    }

    [Property(MaxTest = 80, QuietOnSuccess = true)]
    public void Dash_parser_throws_only_MediaParseException(string text)
    {
        TryParse(() => DashParser.Parse(text, _manifest));
    }

    private static void TryParse(Action action)
    {
        try
        {
            action();
        }
        catch (MediaParseException)
        {
        }
    }

    private static string Fixture(params string[] parts) =>
        File.ReadAllText(Path.Combine(FindRoot(), "tests", "fixtures", "media", Path.Combine(parts)));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kasi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kasi.slnx not found.");
    }
}
