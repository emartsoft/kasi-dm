using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;

namespace Kasi.Media.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-003")]
[Trait("TC", "TC-MED-003")]
public sealed class MediaJobRunnerTests : IAsyncLifetime, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-media-jobs", Guid.NewGuid().ToString("N"));
    private readonly JobId _jobId = JobId.New(TimeProvider.System);
    private TestServerHost _host = null!;
    private SqliteConnectionFactory _factory = null!;
    private JobRepository _jobs = null!;
    private MediaRepository _media = null!;

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
    public async Task Clear_hls_downloads_in_parallel_and_joins_plaintext_in_order()
    {
        MediaSourceLoader.Playable playable;
        using (var loader = new MediaSourceLoader())
        {
            playable = (MediaSourceLoader.Playable)await loader.LoadAsync(
                MediaKind.Hls, Url("/hls/clear/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);
        }

        _host.ResetMetrics();
        var runner = new MediaJobRunner(_media, _jobs);
        var progress = new List<MediaJobProgress>();

        var result = await runner.RunAsync(playable, Options(parallelism: 3, progress: progress.Add), TestContext.Current.CancellationToken);

        result.Streams.Should().HaveCount(2);
        var video = result.Streams.Single(stream => stream.Role == "video");
        var audio = result.Streams.Single(stream => stream.Role == "audio");
        (await File.ReadAllBytesAsync(video.Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("720p"));
        (await File.ReadAllBytesAsync(audio.Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("audio"));
        _host.Metrics.RequestsForPath("/hls/clear/720p/seg0.ts").Should().Be(1);
        _host.Metrics.PeakConcurrentConnections.Should().BeLessThanOrEqualTo(6);
        progress[^1].CompletedSegments.Should().Be(12);

        var job = await _jobs.GetAsync(_jobId, TestContext.Current.CancellationToken);
        job!.State.Should().Be(JobState.Muxing);
        job.DoneBytes.Should().Be(ExpectedHls("720p").Length + ExpectedHls("audio").Length);
        Directory.EnumerateFiles(Path.Combine(_folder, "media"), "*.part", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Dash_download_puts_the_init_segment_first()
    {
        using var loader = new MediaSourceLoader();
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(
            MediaKind.Dash, Url("/dash/clear/manifest.mpd"), cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);

        var result = await runner.RunAsync(playable, Options(parallelism: 2), TestContext.Current.CancellationToken);

        var video = result.Streams.Single(stream => stream.Role == "video");
        var expected = StreamFixtures.DashInit("video-720p")
            .Concat(Enumerable.Range(1, StreamFixtures.SegmentCount).SelectMany(index => StreamFixtures.DashSegment("video-720p", index)))
            .ToArray();
        (await File.ReadAllBytesAsync(video.Path, TestContext.Current.CancellationToken)).Should().Equal(expected);
    }

    [Fact]
    public async Task Aes128_is_decrypted_before_the_ordered_join()
    {
        await using var tlsHost = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid },
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = TrustingClient(tlsHost);
        using var loader = new MediaSourceLoader(client);
        var manifest = new Uri(tlsHost.BaseAddress, "/hls/aes128/master.m3u8");
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, manifest, cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);
        var options = Options(parallelism: 2) with
        {
            ManifestUri = manifest,
            PoolOptions = new HttpHostPoolOptions { CustomTrustRoots = new X509Certificate2Collection(tlsHost.RootCertificate!) },
        };

        var result = await runner.RunAsync(playable, options, TestContext.Current.CancellationToken);

        var video = result.Streams.Single(stream => stream.Role == "video");
        (await File.ReadAllBytesAsync(video.Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("720p"));
        (await File.ReadAllBytesAsync(result.Streams.Single(stream => stream.Role == "audio").Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("audio"));
        tlsHost.Metrics.RequestsForPath("/hls/aes128/key.bin").Should().Be(1);
    }

    [Fact]
    public async Task Cancel_and_resume_skips_completed_segments_and_cleans_part_files()
    {
        using var loader = new MediaSourceLoader();
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(
            MediaKind.Hls, Url("/hls/clear/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);
        using var cts = new CancellationTokenSource();
        var options = Options(parallelism: 1, progress: progress =>
        {
            if (progress.CompletedSegments >= 2)
            {
                cts.Cancel();
            }
        });

        Func<Task> cancelled = () => runner.RunAsync(playable, options, cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var partial = Path.Combine(_folder, "media", _jobId.ToString(), "0", "999.part");
        await File.WriteAllTextAsync(partial, "stray", TestContext.Current.CancellationToken);
        var completedBefore = (await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken))
            .Where(segment => segment.Completed)
            .ToArray();
        completedBefore.Should().NotBeEmpty();
        _host.ResetMetrics();
        var resume = await runner.RunAsync(playable, Options(parallelism: 2), TestContext.Current.CancellationToken);

        (await File.ReadAllBytesAsync(resume.Streams.Single(stream => stream.Role == "video").Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("720p"));
        File.Exists(partial).Should().BeFalse();
        (await _jobs.GetAsync(_jobId, TestContext.Current.CancellationToken))!.State.Should().Be(JobState.Muxing);
        foreach (var segment in completedBefore)
        {
            _host.Metrics.RequestsForPath(new Uri(segment.Url).AbsolutePath).Should().Be(0);
        }
    }

    [Theory]
    [MemberData(nameof(ChaosProfiles))]
    public async Task A_chaos_fault_on_one_segment_is_retried_and_the_capture_finishes(ChaosProfile profile)
    {
        MediaSourceLoader.Playable playable;
        using (var loader = new MediaSourceLoader())
        {
            playable = (MediaSourceLoader.Playable)await loader.LoadAsync(
                MediaKind.Hls, Url("/hls/clear/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);
        }

        _host.Chaos.DefaultProfile = profile;
        try
        {
            var runner = new MediaJobRunner(_media, _jobs);
            var result = await runner.RunAsync(playable, Options(parallelism: 1), TestContext.Current.CancellationToken);

            var video = result.Streams.Single(stream => stream.Role == "video");
            (await File.ReadAllBytesAsync(video.Path, TestContext.Current.CancellationToken)).Should().Equal(ExpectedHls("720p"));
        }
        finally
        {
            _host.Chaos.Clear();
        }
    }

    public static TheoryData<ChaosProfile> ChaosProfiles => new()
    {
        new ChaosProfile { ResetAfterBytes = 1024, ResetOnRequest = 0 },
        new ChaosProfile { StatusCode = 503, StatusCount = 1, RetryAfterSeconds = 0 },
        new ChaosProfile { TruncateAfterBytes = 1024, TruncateOnRequest = 0 },
    };

    [Fact]
    public async Task A_disk_path_that_cannot_be_created_fails_cleanly()
    {
        using var loader = new MediaSourceLoader();
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(
            MediaKind.Hls, Url("/hls/clear/master.m3u8"), cancellationToken: TestContext.Current.CancellationToken);
        var blocked = Path.Combine(_folder, "blocked");
        await File.WriteAllTextAsync(blocked, "not a directory", TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs);

        Func<Task> run = () => runner.RunAsync(playable, Options(parallelism: 1) with { MediaRoot = blocked }, TestContext.Current.CancellationToken);

        var failure = (await run.Should().ThrowAsync<MediaJobException>()).Which;
        failure.Kind.Should().BeOneOf(MediaJobErrorKind.Disk, MediaJobErrorKind.DiskFull);
    }

    private MediaJobOptions Options(int parallelism, Action<MediaJobProgress>? progress = null) => new()
    {
        JobId = _jobId,
        ManifestUri = Url("/hls/clear/master.m3u8"),
        MediaRoot = Path.Combine(_folder, "media"),
        Parallelism = parallelism,
        CompletionInterval = TimeSpan.FromMilliseconds(20),
        Proxy = new ProxySettings { Mode = ProxyMode.None },
        RetryPolicy = new TransferRetryPolicy { BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(20) },
        Progress = progress,
    };

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

    private static byte[] ExpectedHls(string rendition) =>
        Enumerable.Range(0, StreamFixtures.SegmentCount).SelectMany(index => StreamFixtures.HlsSegment(rendition, index)).ToArray();

    private Uri Url(string path) => new(_host.BaseAddress, path);
}
