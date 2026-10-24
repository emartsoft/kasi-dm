using System.Net;
using System.Reflection;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.TestServer;

namespace Kasi.Media.Tests;

/// <summary>Regression tests for the M3.3 review: byte-range init, referrer origin, fail-fast, shared runner, flush and resume cost.</summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-003")]
[Trait("TC", "TC-MED-003")]
public sealed class MediaJobRunnerRobustnessTests : IAsyncLifetime, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-media-robust", Guid.NewGuid().ToString("N"));
    private readonly JobId _jobId = JobId.New(TimeProvider.System);
    private TestServerHost _host = null!;
    private JobRepository _jobs = null!;
    private MediaRepository _media = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var factory = new SqliteConnectionFactory(Path.Combine(_folder, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
        _jobs = new JobRepository(factory);
        _media = new MediaRepository(factory);
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
    public async Task A_byte_range_init_segment_fetches_only_its_range_and_the_page_referrer_follows_the_segment_origin()
    {
        var file = Url("/files/rng/4096");
        var playable = await LoadAsync(
            $"#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MAP:URI=\"{file}\",BYTERANGE=\"100@0\"\n" +
            $"#EXTINF:4,\n#EXT-X-BYTERANGE:1000@100\n{file}\n#EXTINF:4,\n#EXT-X-BYTERANGE:1000\n{file}\n#EXT-X-ENDLIST\n");
        var options = Options(parallelism: 2) with
        {
            ManifestUri = new Uri("https://cdn.example/m.m3u8"),
            RequestContext = new MediaRequestContext(Referrer: new Uri("https://page.example/watch")),
        };
        _host.ResetMetrics();

        var result = await new MediaJobRunner(_media, _jobs).RunAsync(playable, options, TestContext.Current.CancellationToken);

        var expected = GeneratedContent.Bytes("rng", 4096)[..2100];
        (await File.ReadAllBytesAsync(result.Streams.Single().Path, TestContext.Current.CancellationToken)).Should().Equal(expected);
        _host.Metrics.RequestsWithReferer.Should().Be(3, "init and both segments live on a different origin from the manifest");
    }

    [Fact]
    public async Task The_first_failed_segment_stops_the_remaining_ones_and_is_the_error_reported()
    {
        var good = Url("/files/ok/100");
        var playable = await LoadAsync(
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n" + Url("/no-such-segment") + "\n" +
            string.Concat(Enumerable.Repeat($"#EXTINF:4,\n{good}\n", 5)) + "#EXT-X-ENDLIST\n");
        _host.ResetMetrics();

        Func<Task> run = () => new MediaJobRunner(_media, _jobs).RunAsync(playable, Options(parallelism: 1), TestContext.Current.CancellationToken);

        var failure = (await run.Should().ThrowAsync<MediaJobException>()).Which;
        failure.Kind.Should().Be(MediaJobErrorKind.Network);
        _host.Metrics.RequestsForPath("/files/ok/100").Should().Be(0, "parallelism 1 means no later segment may start after the failure");
        (await _jobs.GetAsync(_jobId, TestContext.Current.CancellationToken))!.State.Should().Be(JobState.Failed);
    }

    [Fact]
    public async Task A_long_completion_interval_does_not_delay_the_end_of_a_run()
    {
        var playable = await LoadAsync("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n" + Url("/files/ok/100") + "\n#EXT-X-ENDLIST\n");
        var options = Options(parallelism: 1) with { CompletionInterval = TimeSpan.FromHours(1) };

        var result = await new MediaJobRunner(_media, _jobs).RunAsync(playable, options, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Streams.Should().ContainSingle();
        (await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken)).Should().OnlyContain(segment => segment.Completed);
    }

    [Fact]
    public async Task A_failing_completion_save_is_reported_as_a_disk_error_and_fails_the_job()
    {
        var playable = await LoadAsync("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n" + Url("/files/ok/100") + "\n#EXT-X-ENDLIST\n");
        var runner = new MediaJobRunner(new FailingCompletions(_media), _jobs);

        Func<Task> run = () => runner.RunAsync(playable, Options(parallelism: 1), TestContext.Current.CancellationToken);

        (await run.Should().ThrowAsync<MediaJobException>()).Which.Kind.Should().Be(MediaJobErrorKind.Disk);
        (await _jobs.GetAsync(_jobId, TestContext.Current.CancellationToken))!.State.Should().Be(JobState.Failed);
    }

    [Fact]
    public void The_runner_keeps_no_mutable_state_between_runs()
    {
        var mutable = typeof(MediaJobRunner)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !field.Name.StartsWith('<'))
            .Select(field => field.Name)
            .ToArray();

        mutable.Should().BeEmpty("one MediaJobRunner serves every media job, so per-run state must live in the run");
    }

    [Fact]
    public async Task Resume_matching_stays_linear_for_a_hundred_thousand_segments()
    {
        var streams = new[] { new MediaStreamRecord(0, "video") };
        var segments = Enumerable.Range(0, 100_000)
            .Select(index => new MediaSegmentRecord(0, index, $"https://media.example/s{index}.ts"))
            .ToArray();

        var matches = await Task.Run(() => MediaJobRunner.Matches(streams, segments, streams, segments), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        matches.Should().BeTrue();
        MediaJobRunner.Matches(streams, segments, streams, [.. segments.Take(99_999), segments[^1] with { Url = "https://media.example/other.ts" }])
            .Should().BeFalse();
    }

    [Fact]
    public void An_aes128_key_without_an_iv_uses_the_media_sequence_number_and_an_explicit_iv_is_kept()
    {
        var playlist = HlsParser.ParseMedia(
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://k.example/key\"\n" +
            "#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"https://k.example/key\",IV=0x000102030405060708090A0B0C0D0E0F\n#EXTINF:4,\nc.ts\n#EXT-X-ENDLIST\n",
            new Uri("https://media.example/v.m3u8"));
        var track = new MediaTrack("t", MediaRole.Video, null, null, null, 1, null, null, 12, false, null, playlist.Segments);

        var plan = MediaJobRunner.BuildPlan([track], 1);

        plan.Segments.Select(segment => segment.Iv).Should().Equal(
            "00000000000000000000000000000007",
            "00000000000000000000000000000008",
            "0x000102030405060708090A0B0C0D0E0F");
    }

    [Fact]
    public void A_byte_range_init_is_planned_with_its_range()
    {
        var init = new MediaInitSegment(new Uri("https://media.example/f.mp4"), new MediaByteRange(10, 90));
        var track = new MediaTrack("t", MediaRole.Video, null, null, null, 1, null, null, 4, false, init, [new MediaSegment(0, new Uri("https://media.example/f.mp4"), 4)]);

        var stream = MediaJobRunner.BuildPlan([track], 1).Streams.Single();

        (stream.InitRangeStart, stream.InitRangeEnd, stream.InitSize).Should().Be((10L, 99L, 90L));
    }

    private async Task<MediaSourceLoader.Playable> LoadAsync(string playlist)
    {
#pragma warning disable CA2000 // HttpClient owns the handler
        using var client = new HttpClient(new StaticHandler(playlist));
#pragma warning restore CA2000
        using var loader = new MediaSourceLoader(client);
        return (MediaSourceLoader.Playable)await loader.LoadAsync(
            MediaKind.Hls, new Uri("https://cdn.example/m.m3u8"), cancellationToken: TestContext.Current.CancellationToken);
    }

    private MediaJobOptions Options(int parallelism) => new()
    {
        JobId = _jobId,
        ManifestUri = Url("/m.m3u8"),
        MediaRoot = Path.Combine(_folder, "media"),
        Parallelism = parallelism,
        CompletionInterval = TimeSpan.FromMilliseconds(20),
        Proxy = new ProxySettings { Mode = ProxyMode.None },
        RetryPolicy = new TransferRetryPolicy { BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(20) },
    };

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private sealed class StaticHandler(string playlist) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playlist, System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl"),
            });
    }

    private sealed class FailingCompletions(IMediaRepository inner) : IMediaRepository
    {
        public Task ReplaceAsync(JobId jobId, IReadOnlyList<MediaStreamRecord> streams, IReadOnlyList<MediaSegmentRecord> segments, long? estimatedTotalBytes, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(jobId, streams, segments, estimatedTotalBytes, cancellationToken);

        public Task<IReadOnlyList<MediaStreamRecord>> LoadStreamsAsync(JobId jobId, CancellationToken cancellationToken = default) =>
            inner.LoadStreamsAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<MediaSegmentRecord>> LoadSegmentsAsync(JobId jobId, CancellationToken cancellationToken = default) =>
            inner.LoadSegmentsAsync(jobId, cancellationToken);

        public Task MarkCompletedAsync(JobId jobId, IReadOnlyList<MediaSegmentCompletion> completed, long? estimatedTotalBytes, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated save failure");

        public Task ResetCompletionAsync(JobId jobId, CancellationToken cancellationToken = default) =>
            inner.ResetCompletionAsync(jobId, cancellationToken);
    }
}
