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

        var cycle = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <BaseURL>https://media.example/dash/manifest.mpd</BaseURL>
              <Period/>
            </MPD>
            """;
        Action cyclic = () => DashParser.Parse(cycle, _manifest);
        cyclic.Should().Throw<MediaParseException>().Which.Error.Should().Be(MediaParseError.CyclicBaseUrl);
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
