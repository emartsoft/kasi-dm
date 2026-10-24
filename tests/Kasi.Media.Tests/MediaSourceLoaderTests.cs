using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kasi.Core;
using Kasi.TestServer;

namespace Kasi.Media.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-005")]
[Trait("TC", "TC-MED-005")]
public sealed class MediaSourceLoaderTests : IAsyncLifetime
{
    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Clear_hls_and_dash_return_playable_and_never_fetch_a_segment()
    {
        using var loader = new MediaSourceLoader();
        var ct = TestContext.Current.CancellationToken;

        var hls = await loader.LoadAsync(MediaKind.Hls, Url("/hls/clear/master.m3u8"), cancellationToken: ct);
        var hlsPlayable = hls.Should().BeOfType<MediaSourceLoader.Playable>().Subject;
        hlsPlayable.Presentation.Variants.Should().HaveCount(2);
        hlsPlayable.Presentation.Variants[0].Audio.Should().NotBeNull();

        var dash = await loader.LoadAsync(MediaKind.Dash, Url("/dash/clear/manifest.mpd"), cancellationToken: ct);
        dash.Should().BeOfType<MediaSourceLoader.Playable>().Which.Presentation.Variants.Should().HaveCount(2);
        SegmentPaths().Should().BeEmpty("the loader must not request any segment, init or key path");
    }

    [Fact]
    public async Task Every_hls_drm_kind_is_refused_with_the_exact_message_and_no_segments()
    {
        using var loader = new MediaSourceLoader();
        var ct = TestContext.Current.CancellationToken;
        foreach (var kind in StreamFixtures.HlsKinds.Where(kind => kind is not "clear" and not "aes128"))
        {
            _host.ResetMetrics();
            var result = await loader.LoadAsync(MediaKind.Hls, Url($"/hls/{kind}/master.m3u8"), cancellationToken: ct);

            var refused = result.Should().BeOfType<MediaSourceLoader.ProtectedResult>().Subject;
            refused.Message.Should().Be(MediaSourceLoader.ProtectedMessage);
            refused.ReasonCode.Should().Be("hls-drm");
            SegmentPaths().Should().BeEmpty($"{kind} must be refused before a segment request");
        }
    }

    [Fact]
    public async Task Every_dash_drm_kind_is_refused_with_the_exact_message_and_no_segments()
    {
        using var loader = new MediaSourceLoader();
        var ct = TestContext.Current.CancellationToken;
        foreach (var kind in StreamFixtures.DashKinds.Where(kind => kind is not "clear" and not "segment-list"))
        {
            _host.ResetMetrics();
            var result = await loader.LoadAsync(MediaKind.Dash, Url($"/dash/{kind}/manifest.mpd"), cancellationToken: ct);

            var refused = result.Should().BeOfType<MediaSourceLoader.ProtectedResult>().Subject;
            refused.Message.Should().Be(MediaSourceLoader.ProtectedMessage);
            refused.ReasonCode.Should().Be("dash-drm");
            SegmentPaths().Should().BeEmpty($"{kind} must be refused before a segment request");
        }
    }

    [Fact]
    public async Task An_aes128_key_over_plain_http_is_refused_but_https_is_playable()
    {
        using (var httpLoader = new MediaSourceLoader())
        {
            var refused = (await httpLoader.LoadAsync(MediaKind.Hls, Url("/hls/aes128/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken))
                .Should().BeOfType<MediaSourceLoader.ProtectedResult>().Subject;
            refused.Message.Should().Be(MediaSourceLoader.ProtectedMessage);
        }

        await using var tlsHost = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid },
            cancellationToken: TestContext.Current.CancellationToken);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(tlsHost.RootCertificate!);
        handler.SslOptions.CertificateChainPolicy = chain;
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var loader = new MediaSourceLoader(client);

        var playable = (await loader.LoadAsync(
            MediaKind.Hls,
            new Uri(tlsHost.BaseAddress, "/hls/aes128/master.m3u8"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<MediaSourceLoader.Playable>().Subject;
        playable.Presentation.Variants.Should().NotBeEmpty();
        tlsHost.Metrics.PathRequests.Keys.Should().NotContain(path => path.Contains("/seg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Page_cookies_are_scoped_to_the_manifest_host_and_auth_gate()
    {
        using var loader = new MediaSourceLoader();
        var without = async () => await loader.LoadAsync(
            MediaKind.Hls,
            Url("/auth/cookie/hls/clear/master.m3u8"),
            cancellationToken: TestContext.Current.CancellationToken);
        await without.Should().ThrowAsync<MediaLoadException>();

        var context = new MediaRequestContext(
            Cookies: [new JobCookie { Name = "kasi_session", Value = "ok", Domain = _host.BaseAddress.Host, Path = "/" }],
            Referrer: new Uri("https://example.test/page"),
            UserAgent: "Kasi-Test/1.0");
        var result = await loader.LoadAsync(MediaKind.Hls, Url("/auth/cookie/hls/clear/master.m3u8"), context, TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.Playable>();
        _host.Metrics.RequestsWithCookie.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Redirected_manifests_resolve_relative_playlists_against_the_final_url()
    {
        using var loader = new MediaSourceLoader();
        var result = await loader.LoadAsync(MediaKind.Hls, Url("/redirect/2/hls/clear/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.Playable>();
        SegmentPaths().Should().BeEmpty();
    }

    [Fact]
    public async Task A_session_key_in_the_master_is_refused_even_when_the_media_playlist_is_clear()
    {
        const string Master = "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n" +
                              "#EXT-X-STREAM-INF:BANDWIDTH=100\nv.m3u8\n";
        const string Media = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n";
#pragma warning disable CA2000 // HttpClient owns the handler
        using var client = new HttpClient(new StaticHandler(request => request.RequestUri!.AbsolutePath.EndsWith("master.m3u8", StringComparison.Ordinal)
            ? Text(Master)
            : Text(Media)));
#pragma warning restore CA2000
        using var loader = new MediaSourceLoader(client);

        var result = await loader.LoadAsync(MediaKind.Hls, new Uri("https://media.example/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.ProtectedResult>().Which.Message.Should().Be(MediaSourceLoader.ProtectedMessage);
    }

    [Fact]
    public async Task A_segment_list_manifest_is_playable()
    {
        using var loader = new MediaSourceLoader();
        var result = await loader.LoadAsync(MediaKind.Dash, Url("/dash/segment-list/manifest.mpd"), cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.Playable>().Which.Presentation.Variants.Should().NotBeEmpty();
        SegmentPaths().Should().BeEmpty();
    }

    [Fact]
    public async Task The_page_referrer_reaches_a_cross_origin_manifest_but_not_a_cross_origin_redirect_target()
    {
        var seen = new List<(string Host, string? Referer)>();
#pragma warning disable CA2000 // HttpClient owns the handler
        using var client = new HttpClient(new StaticHandler(request =>
        {
            seen.Add((request.RequestUri!.Host, request.Headers.Referrer?.ToString()));
            if (request.RequestUri.Host == "cdn.example")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://other.example/media.m3u8");
                return redirect;
            }

            return Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n");
        }));
#pragma warning restore CA2000
        using var loader = new MediaSourceLoader(client);
        var context = new MediaRequestContext(Referrer: new Uri("https://page.example/watch"));

        var result = await loader.LoadAsync(MediaKind.Hls, new Uri("https://cdn.example/m.m3u8"), context, TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.Playable>();
        seen.Should().Equal(("cdn.example", "https://page.example/watch"), ("other.example", null));
    }

    [Fact]
    public async Task Many_playlists_cannot_exceed_the_total_segment_budget()
    {
        var master = "#EXTM3U\n" + string.Concat(Enumerable.Range(1, 3).Select(i => $"#EXT-X-STREAM-INF:BANDWIDTH={i * 100}\nv{i}.m3u8\n"));
        var media = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n" + string.Concat(Enumerable.Repeat("#EXTINF:1,\ns.ts\n", 99_000)) + "#EXT-X-ENDLIST\n";
#pragma warning disable CA2000 // HttpClient owns the handler
        using var client = new HttpClient(new StaticHandler(request => Text(request.RequestUri!.AbsolutePath.EndsWith("master.m3u8", StringComparison.Ordinal) ? master : media)));
#pragma warning restore CA2000
        using var loader = new MediaSourceLoader(client);

        var load = async () => await loader.LoadAsync(MediaKind.Hls, new Uri("https://media.example/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);

        (await load.Should().ThrowAsync<MediaLoadException>()).Which.Error.Should().Be(MediaLoadError.TooLarge);
    }

    [Fact]
    public async Task A_variant_playlist_that_switches_to_sample_aes_midway_is_refused()
    {
        const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nv.m3u8\n";
        const string Media = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\na.ts\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\nb.ts\n#EXT-X-ENDLIST\n";
#pragma warning disable CA2000 // HttpClient owns the handler
        using var client = new HttpClient(new StaticHandler(request => Text(request.RequestUri!.AbsolutePath.EndsWith("master.m3u8", StringComparison.Ordinal) ? Master : Media)));
#pragma warning restore CA2000
        using var loader = new MediaSourceLoader(client);

        var result = await loader.LoadAsync(MediaKind.Hls, new Uri("https://media.example/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeOfType<MediaSourceLoader.ProtectedResult>();
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl"),
    };

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private IReadOnlyList<string> SegmentPaths() =>
        [.. _host.Metrics.PathRequests.Keys.Where(path =>
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("init.mp4", StringComparison.OrdinalIgnoreCase))];
}
