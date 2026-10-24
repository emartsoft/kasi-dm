using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.TestServer;

namespace Kasi.Media.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-004")]
[Trait("TC", "TC-MED-004")]
[Trait("Requirement", "KASI-MED-006")]
[Trait("TC", "TC-MED-006")]
public sealed class MediaMuxerTests : IAsyncLifetime, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-media-mux", Guid.NewGuid().ToString("N"));
    private readonly JobId _jobId = JobId.New(TimeProvider.System);
    private TestServerHost _host = null!;
    private SqliteConnectionFactory _factory = null!;
    private JobRepository _jobs = null!;
    private MediaRepository _media = null!;
    private FfmpegOptions _ffmpeg = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _factory = new SqliteConnectionFactory(Path.Combine(_folder, "kasi.db"));
        await new MigrationRunner(_factory).MigrateAsync(TestContext.Current.CancellationToken);
        _jobs = new JobRepository(_factory);
        _media = new MediaRepository(_factory);
        await _jobs.AddAsync(new Job
        {
            Id = _jobId,
            Kind = JobKind.Media,
            Url = "https://media.example/master.m3u8",
            FileName = "capture.mp4",
            Folder = _folder,
            QueueId = 1,
            QueuePos = 1,
            State = JobState.Downloading,
            CreatedUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Hls_muxes_to_mp4_and_probes_24_seconds()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var output = Path.Combine(_folder, "out", "capture.mp4");
        var muxed = await Muxer().MuxAsync(result, new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);

        muxed.Container.Should().Be("mp4");
        muxed.DurationSeconds.Should().BeApproximately(24, 1);
        muxed.WorkDirectoryDeleted.Should().BeTrue();
        File.Exists(output).Should().BeTrue();
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, TestContext.Current.CancellationToken);
        probe.DurationSeconds.Should().BeApproximately(24, 1);
        probe.Streams.Should().Contain(stream => stream.CodecType == "video");
        probe.Streams.Should().Contain(stream => stream.CodecType == "audio");
    }

    [Fact]
    public async Task Hls_muxes_to_mkv_when_requested_or_when_mp4_cannot_hold_the_codecs()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var output = Path.Combine(_folder, "out", "capture.mkv");
        var muxed = await Muxer().MuxAsync(result, new MediaMuxOptions
        {
            OutputPath = output,
            Container = MediaMuxContainer.Matroska,
        }, TestContext.Current.CancellationToken);

        muxed.Container.Should().Be("mkv");
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, TestContext.Current.CancellationToken);
        probe.DurationSeconds.Should().BeApproximately(24, 1);
        probe.Streams.Should().Contain(stream => stream.CodecType == "video");
    }

    [Fact]
    public async Task Dash_fmp4_muxes_to_mp4()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/dash/muxable/manifest.mpd");
        var output = Path.Combine(_folder, "out", "dash.mp4");
        var muxed = await Muxer().MuxAsync(result, new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);

        muxed.Container.Should().Be("mp4");
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, TestContext.Current.CancellationToken);
        probe.DurationSeconds.Should().BeApproximately(24, 1);
        probe.Streams.Should().Contain(stream => stream.CodecType == "video");
    }

    [Fact]
    public async Task Aes128_hls_muxes_to_a_playable_mp4()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        await using var tlsHost = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid },
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = TrustingClient(tlsHost);
        using var loader = new MediaSourceLoader(client);
        var manifest = new Uri(tlsHost.BaseAddress, "/hls/muxable-aes128/master.m3u8");
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, manifest, cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);
        var result = await runner.RunAsync(playable, new MediaJobOptions
        {
            JobId = _jobId,
            ManifestUri = manifest,
            MediaRoot = Path.Combine(_folder, "media"),
            Parallelism = 2,
            CompletionInterval = TimeSpan.FromMilliseconds(20),
            Proxy = new ProxySettings { Mode = ProxyMode.None },
            RetryPolicy = new TransferRetryPolicy { BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(20) },
            PoolOptions = new HttpHostPoolOptions { CustomTrustRoots = new X509Certificate2Collection(tlsHost.RootCertificate!) },
        }, TestContext.Current.CancellationToken);

        var output = Path.Combine(_folder, "out", "aes.mp4");
        var muxed = await Muxer().MuxAsync(result, new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);

        muxed.DurationSeconds.Should().BeApproximately(24, 1);
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, TestContext.Current.CancellationToken);
        probe.Streams.Should().Contain(stream => stream.CodecType == "video");
        probe.Streams.Should().Contain(stream => stream.CodecType == "audio");
    }

    [Fact]
    public async Task A_duration_mismatch_keeps_the_work_folder_and_fails()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var longer = result.Variant!.Video with { DurationSeconds = 30 };
        var mismatch = result with { Variant = result.Variant with { Video = longer } };
        var output = Path.Combine(_folder, "out", "mismatch.mp4");

        Func<Task> mux = () => Muxer().MuxAsync(mismatch, new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);

        var failure = (await mux.Should().ThrowAsync<MediaMuxException>()).Which;
        failure.Kind.Should().Be(MediaMuxErrorKind.DurationMismatch);
        Directory.Exists(result.WorkDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Audio_only_muxes_to_m4a()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var audioPath = Path.Combine(_folder, "audio.m4a");
        File.Copy(Path.Combine(MuxableFixturesSource(), "audio.m4a"), audioPath);
        var work = Path.Combine(_folder, "audio-work");
        Directory.CreateDirectory(work);
        var track = new MediaTrack("audio", MediaRole.Audio, "en", "mp4a.40.2", "audio/mp4", 48000, null, null, 24, false, null, []);
        var variant = new MediaVariant(track, track, null);
        var result = new MediaJobResult(
            _jobId,
            [new MediaJoinedTrack(0, "audio", audioPath, new FileInfo(audioPath).Length, "audio/mp4")],
            new FileInfo(audioPath).Length,
            variant,
            work);
        var output = Path.Combine(_folder, "out", "audio.m4a");

        var muxed = await Muxer().MuxAsync(result, new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);

        muxed.Container.Should().Be("m4a");
        Directory.Exists(work).Should().BeFalse();
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, TestContext.Current.CancellationToken);
        probe.DurationSeconds.Should().BeApproximately(24, 1);
        probe.Streams.Should().OnlyContain(stream => stream.CodecType == "audio");
    }

    private static string MuxableFixturesSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kasi.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "fixtures", "media", "muxable");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root (Kasi.slnx) was not found.");
    }

    private bool TryGetFfmpeg()
    {
        if (FfmpegLocator.TryFind(out var options))
        {
            _ffmpeg = options;
            return true;
        }

        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            throw new InvalidOperationException("FFmpeg/ffprobe are required in CI (KASI_FFMPEG/KASI_FFPROBE or PATH).");
        }

        return false;
    }

    private MediaMuxer Muxer() => new(new FfmpegRunner(_ffmpeg));

    private async Task<MediaJobResult> DownloadAsync(string path)
    {
        using var loader = new MediaSourceLoader();
        var manifest = new Uri(_host.BaseAddress, path);
        var kind = path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls;
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(kind, manifest, cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);
        return await runner.RunAsync(playable, new MediaJobOptions
        {
            JobId = _jobId,
            ManifestUri = manifest,
            MediaRoot = Path.Combine(_folder, "media"),
            Parallelism = 2,
            CompletionInterval = TimeSpan.FromMilliseconds(20),
            Proxy = new ProxySettings { Mode = ProxyMode.None },
            RetryPolicy = new TransferRetryPolicy { BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(20) },
        }, TestContext.Current.CancellationToken);
    }

    private static HttpClient TrustingClient(TestServerHost host)
    {
#pragma warning disable CA2000 // HttpClient owns the handler
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
#pragma warning restore CA2000
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(host.RootCertificate!);
        handler.SslOptions.CertificateChainPolicy = chain;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
