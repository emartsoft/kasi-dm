using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.TestServer;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Media.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-007")]
[Trait("TC", "TC-MED-007")]
public sealed class LiveMediaTests : IAsyncLifetime, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-live-media", Guid.NewGuid().ToString("N"));
    private readonly JobId _jobId = JobId.New(TimeProvider.System);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
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
        _host = await TestServerHost.StartAsync(_clock, TestContext.Current.CancellationToken);
        await _jobs.AddAsync(new Job
        {
            Id = _jobId,
            Kind = JobKind.Media,
            Url = new Uri(_host.BaseAddress, "/hls/live/index.m3u8").AbsoluteUri,
            FileName = "live.mp4",
            Folder = _folder,
            QueueId = 1,
            QueuePos = 1,
            State = JobState.Downloading,
            CreatedUtc = _clock.GetUtcNow(),
        }, TestContext.Current.CancellationToken);
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
    public async Task Stop_finishes_the_collected_segments_and_muxes_once()
    {
        using var stop = new CancellationTokenSource();
        using var loader = new MediaSourceLoader();
        var uri = new Uri(_host.BaseAddress, "/hls/live/index.m3u8");
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, uri,
            cancellationToken: TestContext.Current.CancellationToken);
        var run = new MediaJobRunner(_media, _jobs, _clock).RunAsync(playable,
            Options(loader, stop.Token), TestContext.Current.CancellationToken);

        await WaitForSegmentAsync(0);
        for (var index = 1; index <= 3; index++)
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            await WaitForSegmentAsync(index);
        }

        await stop.CancelAsync();
        var result = await run;
        result.Streams.Single().Bytes.Should().Be(Enumerable.Range(0, 4).Sum(index => MuxableFixtures.LiveSegment(index).Length));
        (await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken)).Should().HaveCount(4);
        (await _jobs.GetAsync(_jobId, TestContext.Current.CancellationToken))!.State.Should().Be(JobState.Muxing);
        _host.Metrics.RequestsForPath("/hls/live/seg0.ts").Should().Be(1);
    }

    [Fact]
    public async Task Endlist_after_150_segments_closes_a_ten_minute_live_window()
    {
        using var loader = new MediaSourceLoader();
        var uri = new Uri(_host.BaseAddress, "/hls/live/index.m3u8");
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, uri,
            cancellationToken: TestContext.Current.CancellationToken);
        var run = new MediaJobRunner(_media, _jobs, _clock).RunAsync(playable,
            Options(loader, CancellationToken.None), TestContext.Current.CancellationToken);

        await WaitForSegmentAsync(0);
        for (var index = 1; index < 150; index++)
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            await WaitForSegmentAsync(index);
        }

        var result = await run;
        result.Streams.Single().Bytes.Should().Be(Enumerable.Range(0, 150).Sum(index => MuxableFixtures.LiveSegment(index).Length));
        (await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken)).Should().HaveCount(150);
        _host.Metrics.RequestsForPath("/hls/live/index.m3u8").Should().BeGreaterThanOrEqualTo(150);
        if (FfmpegLocator.TryFind(out var ffmpeg))
        {
            var output = Path.Combine(_folder, "live.mp4");
            var muxed = await new MediaMuxer(new FfmpegRunner(ffmpeg)).MuxAsync(result,
                new MediaMuxOptions { OutputPath = output }, TestContext.Current.CancellationToken);
            muxed.DurationSeconds.Should().BeApproximately(600, 1);
            File.Exists(muxed.OutputPath).Should().BeTrue();
        }
        else if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            throw new InvalidOperationException("FFmpeg/ffprobe are required in CI for the ten-minute live output test.");
        }
    }

    [Fact]
    public async Task Restart_keeps_completed_segments_and_appends_the_next_window()
    {
        using var loader = new MediaSourceLoader();
        using var interrupted = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var uri = new Uri(_host.BaseAddress, "/hls/live/index.m3u8");
        var playable = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, uri,
            cancellationToken: TestContext.Current.CancellationToken);
        var runner = new MediaJobRunner(_media, _jobs, _clock);
        var first = runner.RunAsync(playable, Options(loader, CancellationToken.None), interrupted.Token);

        await WaitForSegmentAsync(0);
        for (var index = 1; index <= 2; index++)
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            await WaitForSegmentAsync(index);
        }

        await interrupted.CancelAsync();
        var wasCancelled = false;
        try
        {
            await first;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }

        wasCancelled.Should().BeTrue();
        var saved = await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken);
        saved.Should().HaveCount(3).And.OnlyContain(segment => segment.Completed);

        var resumed = (MediaSourceLoader.Playable)await loader.LoadAsync(MediaKind.Hls, uri,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = runner.RunAsync(resumed, Options(loader, stop.Token), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(4));
        await WaitForSegmentAsync(3);
        await stop.CancelAsync();
        var result = await second;

        result.Streams.Single().Bytes.Should().Be(Enumerable.Range(0, 4).Sum(index => MuxableFixtures.LiveSegment(index).Length));
        _host.Metrics.RequestsForPath("/hls/live/seg0.ts").Should().Be(1);
        (await _media.LoadSegmentsAsync(_jobId, TestContext.Current.CancellationToken)).Should().HaveCount(4);
    }

    private MediaJobOptions Options(MediaSourceLoader loader, CancellationToken stop) => new()
    {
        JobId = _jobId,
        ManifestUri = new Uri(_host.BaseAddress, "/hls/live/index.m3u8"),
        MediaRoot = Path.Combine(_folder, "media"),
        Parallelism = 1,
        CompletionInterval = TimeSpan.FromMilliseconds(20),
        Proxy = new ProxySettings { Mode = ProxyMode.None },
        LiveLoader = loader,
        LiveStopToken = stop,
    };

    private async Task WaitForSegmentAsync(int index)
    {
        var path = $"/hls/live/seg{index}.ts";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (_host.Metrics.RequestsForPath(path) == 0)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"The live runner did not request segment {index}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // The request counter runs before the segment is flushed and the next poll timer is installed.
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }
}
