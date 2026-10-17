using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Kasi.TestServer.Tests;

[Trait("Category", "Integration")]
public sealed class StreamingEndpointTests : IAsyncLifetime
{
    private static readonly XNamespace _mpd = "urn:mpeg:dash:schema:mpd:2011";

    private TestServerHost _host = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);
        _client = TestClient.Create(_host);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    public static TheoryData<string> HlsKinds => new(StreamFixtures.HlsKinds);

    public static TheoryData<string> DashKinds => new(StreamFixtures.DashKinds);

    [Theory]
    [MemberData(nameof(HlsKinds))]
    public async Task Hls_master_lists_two_video_variants_and_one_audio_group(string kind)
    {
        var master = await _client.GetStringAsync($"/hls/{kind}/master.m3u8", TestClient.Ct);

        master.Should().StartWith("#EXTM3U");
        master.Split('\n').Count(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)).Should().Be(2);
        master.Should().Contain("RESOLUTION=640x360").And.Contain("RESOLUTION=1280x720").And.Contain("TYPE=AUDIO");
    }

    [Theory]
    [MemberData(nameof(HlsKinds))]
    public async Task Hls_media_playlist_has_six_segments_an_end_marker_and_the_kinds_key_line(string kind)
    {
        var playlist = await _client.GetStringAsync($"/hls/{kind}/360p/index.m3u8", TestClient.Ct);
        var keyLine = playlist.Split('\n').SingleOrDefault(l => l.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal));

        playlist.Split('\n').Count(l => l.EndsWith(".ts", StringComparison.Ordinal)).Should().Be(StreamFixtures.SegmentCount);
        playlist.Should().Contain("#EXT-X-ENDLIST");
        switch (kind)
        {
            case "clear":
                keyLine.Should().BeNull();
                break;
            case "aes128":
                keyLine.Should().Contain("METHOD=AES-128").And.NotContain("KEYFORMAT");
                break;
            case "sample-aes":
                keyLine.Should().Contain("METHOD=SAMPLE-AES,");
                break;
            case "sample-aes-ctr":
                keyLine.Should().Contain("METHOD=SAMPLE-AES-CTR");
                break;
            default:
                keyLine.Should().Contain("METHOD=AES-128").And.Contain("KEYFORMAT=", because: "the key format names a DRM system even though the method is AES-128");
                break;
        }
    }

    [Theory]
    [InlineData("360p")]
    [InlineData("720p")]
    [InlineData("audio")]
    public async Task Clear_hls_segments_are_the_expected_bytes(string rendition)
    {
        for (var i = 0; i < StreamFixtures.SegmentCount; i++)
        {
            var body = await _client.GetByteArrayAsync($"/hls/clear/{rendition}/seg{i}.ts", TestClient.Ct);

            body.Should().Equal(StreamFixtures.HlsSegment(rendition, i));
        }
    }

    [Fact]
    public async Task Aes128_hls_segments_decrypt_with_the_served_key_to_the_plaintext()
    {
        var key = await _client.GetByteArrayAsync("/hls/aes128/key.bin", TestClient.Ct);
        using var aes = Aes.Create();
        aes.Key = key;

        for (var i = 0; i < StreamFixtures.SegmentCount; i++)
        {
            var encrypted = await _client.GetByteArrayAsync($"/hls/aes128/720p/seg{i}.ts", TestClient.Ct);
            var iv = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(12), i);

            encrypted.Should().NotEqual(StreamFixtures.HlsSegment("720p", i));
            aes.DecryptCbc(encrypted, iv).Should().Equal(StreamFixtures.HlsSegment("720p", i));
        }
    }

    [Theory]
    [InlineData("/hls/nonsense/master.m3u8")]
    [InlineData("/hls/clear/8k/index.m3u8")]
    [InlineData("/hls/clear/360p/seg6.ts")]
    [InlineData("/dash/nonsense/manifest.mpd")]
    [InlineData("/dash/clear/video-360p/seg-0.m4s")]
    [InlineData("/dash/clear/video-360p/seg-7.m4s")]
    public async Task Unknown_kinds_renditions_and_segments_are_404(string url)
    {
        using var response = await _client.GetAsync(url, TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(DashKinds))]
    public async Task Dash_manifest_is_valid_XML_with_two_adaptation_sets_and_three_representations(string kind)
    {
        using var response = await _client.GetAsync($"/dash/{kind}/manifest.mpd", TestClient.Ct);
        var manifest = XDocument.Parse(await response.Content.ReadAsStringAsync(TestClient.Ct));

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/dash+xml");
        manifest.Descendants(_mpd + "AdaptationSet").Should().HaveCount(2);
        manifest.Descendants(_mpd + "Representation").Select(r => (string?)r.Attribute("id")).Should().Equal(StreamFixtures.DashRepresentations);
        manifest.Root!.Attribute("mediaPresentationDuration")!.Value.Should().Be("PT24S");
    }

    [Theory]
    [InlineData("clear", null)]
    [InlineData("segment-list", null)]
    [InlineData("widevine", "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed")]
    [InlineData("playready", "urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95")]
    [InlineData("fairplay", "urn:uuid:94ce86fb-07ff-4f43-adb8-93d2fa968ca2")]
    [InlineData("clearkey", "urn:uuid:e2719d58-a985-b3c9-781a-b030af78d30e")]
    public async Task Dash_content_protection_appears_exactly_for_the_protected_kinds(string kind, string? scheme)
    {
        var manifest = XDocument.Parse(await _client.GetStringAsync($"/dash/{kind}/manifest.mpd", TestClient.Ct));

        var schemes = manifest.Descendants(_mpd + "ContentProtection").Select(c => (string?)c.Attribute("schemeIdUri")).Distinct().ToList();

        schemes.Should().Equal(scheme is null ? [] : [scheme]);
    }

    [Fact]
    public async Task Dash_segment_template_resolves_to_fetchable_init_and_segments()
    {
        var manifest = XDocument.Parse(await _client.GetStringAsync("/dash/clear/manifest.mpd", TestClient.Ct));
        var template = manifest.Descendants(_mpd + "SegmentTemplate").First();

        template.Attribute("startNumber")!.Value.Should().Be("1");
        foreach (var representation in StreamFixtures.DashRepresentations)
        {
            var init = template.Attribute("initialization")!.Value.Replace("$RepresentationID$", representation, StringComparison.Ordinal);
            (await _client.GetByteArrayAsync($"/dash/clear/{init}", TestClient.Ct)).Should().Equal(StreamFixtures.DashInit(representation));
            for (var n = 1; n <= StreamFixtures.SegmentCount; n++)
            {
                var media = template.Attribute("media")!.Value
                    .Replace("$RepresentationID$", representation, StringComparison.Ordinal)
                    .Replace("$Number$", n.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                (await _client.GetByteArrayAsync($"/dash/clear/{media}", TestClient.Ct)).Should().Equal(StreamFixtures.DashSegment(representation, n));
            }
        }
    }

    [Fact]
    public async Task Dash_segment_list_names_an_init_and_six_segment_urls_per_representation()
    {
        var manifest = XDocument.Parse(await _client.GetStringAsync("/dash/segment-list/manifest.mpd", TestClient.Ct));

        manifest.Descendants(_mpd + "SegmentTemplate").Should().BeEmpty();
        foreach (var representation in manifest.Descendants(_mpd + "Representation"))
        {
            var id = (string)representation.Attribute("id")!;
            var list = representation.Element(_mpd + "SegmentList")!;
            var urls = list.Elements(_mpd + "SegmentURL").Select(u => (string)u.Attribute("media")!).ToList();

            ((string)list.Element(_mpd + "Initialization")!.Attribute("sourceURL")!).Should().Be($"{id}/init.mp4");
            urls.Should().HaveCount(StreamFixtures.SegmentCount);
            (await _client.GetByteArrayAsync($"/dash/segment-list/{urls[^1]}", TestClient.Ct)).Should().Equal(StreamFixtures.DashSegment(id, StreamFixtures.SegmentCount));
        }
    }
}
