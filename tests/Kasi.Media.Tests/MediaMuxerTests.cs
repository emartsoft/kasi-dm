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
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, cancellationToken: TestContext.Current.CancellationToken);
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
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, cancellationToken: TestContext.Current.CancellationToken);
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
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, cancellationToken: TestContext.Current.CancellationToken);
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
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, cancellationToken: TestContext.Current.CancellationToken);
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
    public async Task A_failed_check_leaves_no_file_at_the_output_path()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var mismatch = result with { Variant = result.Variant! with { Video = result.Variant.Video with { DurationSeconds = 30 } } };
        var directory = Path.Combine(_folder, "out-failed");

        Func<Task> mux = () => Muxer().MuxAsync(mismatch, new MediaMuxOptions { OutputPath = Path.Combine(directory, "x.mp4") }, TestContext.Current.CancellationToken);

        await mux.Should().ThrowAsync<MediaMuxException>();
        Directory.EnumerateFiles(directory).Should().BeEmpty("neither the output nor its .part file may remain after a failed check");
    }

    [Fact]
    public async Task An_existing_file_is_never_replaced_and_dots_in_the_title_are_kept()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var directory = Path.Combine(_folder, "out-unique");
        Directory.CreateDirectory(directory);
        var taken = Path.Combine(directory, "My.Show.S01E02.mp4");
        await File.WriteAllTextAsync(taken, "keep me", TestContext.Current.CancellationToken);

        var muxed = await Muxer().MuxAsync(
            result, new MediaMuxOptions { OutputPath = Path.Combine(directory, "My.Show.S01E02") }, TestContext.Current.CancellationToken);

        muxed.OutputPath.Should().Be(Path.Combine(directory, "My.Show.S01E02 (1).mp4"));
        (await File.ReadAllTextAsync(taken, TestContext.Current.CancellationToken)).Should().Be("keep me");
    }

    [Fact]
    public async Task A_manifest_without_codecs_is_classified_by_probing_the_joined_file()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var result = await DownloadAsync("/hls/muxable/master.m3u8");
        var variant = result.Variant!;
        var unknown = result with
        {
            Variant = variant with
            {
                Video = variant.Video with { Codecs = null },
                Audio = variant.Audio is { } audio ? audio with { Codecs = null } : null,
            },
        };

        var muxed = await Muxer().MuxAsync(
            unknown, new MediaMuxOptions { OutputPath = Path.Combine(_folder, "out-probe", "capture") }, TestContext.Current.CancellationToken);

        muxed.Container.Should().Be("mp4", "the joined H.264/AAC stream fits MP4 once its codecs are read from the file");
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
        var probe = await new FfmpegRunner(_ffmpeg).ProbeAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        probe.DurationSeconds.Should().BeApproximately(24, 1);
        probe.Streams.Should().OnlyContain(stream => stream.CodecType == "audio");
    }

    private sealed class FakeScanner(ScanResult answer) : IFileScanner
    {
        public bool IsEnabled => true;

        public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private MediaJobResult AudioResult(string name)
    {
        var audioPath = Path.Combine(_folder, name + ".m4a");
        File.Copy(Path.Combine(MuxableFixturesSource(), "audio.m4a"), audioPath);
        var work = Path.Combine(_folder, name + "-work");
        Directory.CreateDirectory(work);
        var track = new MediaTrack("audio", MediaRole.Audio, "en", "mp4a.40.2", "audio/mp4", 48000, null, null, 24, false, null, []);
        return new MediaJobResult(
            _jobId,
            [new MediaJoinedTrack(0, "audio", audioPath, new FileInfo(audioPath).Length, "audio/mp4")],
            new FileInfo(audioPath).Length,
            new MediaVariant(track, track, null),
            work);
    }

    [Trait("Requirement", "KASI-CAT-003")]
    [Trait("TC", "TC-CAT-003")]
    [Fact]
    public async Task A_muxed_file_is_scanned_and_carries_the_mark_of_the_web()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var log = new List<string>();
        var output = Path.Combine(_folder, "out", "marked.m4a");

        var muxed = await Muxer().MuxAsync(
            AudioResult("marked"), new MediaMuxOptions { OutputPath = output, Scanner = new FakeScanner(ScanResult.Clean), Log = log.Add }, TestContext.Current.CancellationToken);

        log.Should().Contain("Scan: clean");
        (await File.ReadAllTextAsync(muxed.OutputPath + ":Zone.Identifier", TestContext.Current.CancellationToken)).Should().Contain("ZoneId=3");
    }

    [Trait("Requirement", "KASI-CAT-004")]
    [Trait("TC", "TC-CAT-004")]
    [Fact]
    public async Task A_flagged_muxed_file_fails_the_job_and_stays_under_its_temporary_name()
    {
        if (!TryGetFfmpeg())
        {
            return;
        }

        var output = Path.Combine(_folder, "out", "flagged.m4a");

        var act = () => Muxer().MuxAsync(
            AudioResult("flagged"), new MediaMuxOptions { OutputPath = output, Scanner = new FakeScanner(ScanResult.Detected) }, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<MediaMuxException>()).Which.Kind.Should().Be(MediaMuxErrorKind.FlaggedByAntivirus);
        File.Exists(output).Should().BeFalse("nothing flagged gets its real name");
        File.Exists(output + ".part.m4a").Should().BeTrue("and Kasi does not delete it: the antivirus has dealt with it");
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
