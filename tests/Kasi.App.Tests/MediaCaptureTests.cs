using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.Ipc.Messages;
using Kasi.Media;
using IpcMediaKind = Kasi.Ipc.Messages.MediaKind;

namespace Kasi.App.Tests;

internal sealed class RecordingPicker : IMediaPicker
{
    public MediaPickerSelection? Selection { get; set; }

    public string? Title { get; private set; }

    public Task<MediaPickerSelection?> PickAsync(MediaPresentation presentation, string title, string? thumbnailUrl, CancellationToken cancellationToken)
    {
        Title = title;
        return Task.FromResult(Selection);
    }
}

internal sealed class RecordingControl : IJobControl
{
    public List<JobId> Started { get; } = [];

    public List<JobId> Paused { get; } = [];

    public Exception? FailStart { get; set; }

    public Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        Started.Add(id);
        return FailStart is null ? Task.CompletedTask : Task.FromException(FailStart);
    }

    public Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        Paused.Add(id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A tiny local HTTP server for the capture tests. Kasi.TestServer is not used here: it needs the ASP.NET Core shared framework,
/// and referencing it would stop the real second-instance test process from finding the hosting assemblies next to Kasi.App.exe.
/// </summary>
internal sealed class LocalStreamServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, CancellationToken, Task>> _routes = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public LocalStreamServer()
    {
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public Uri BaseAddress { get; }

    public ConcurrentQueue<string> Requests { get; } = new();

    public void Text(string path, string body) =>
        Route(path, async (context, ct) =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            await context.Response.OutputStream.WriteAsync(bytes, ct);
        });

    public void File(string path, string file) =>
        Route(path, async (context, ct) =>
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(file, ct);
            context.Response.ContentType = "application/octet-stream";
            await context.Response.OutputStream.WriteAsync(bytes, ct);
        });

    public void Route(string path, Func<HttpListenerContext, CancellationToken, Task> handler) => _routes[path] = handler;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            Requests.Enqueue(path);
            if (_routes.TryGetValue(path, out var route))
            {
                await route(context, _stop.Token);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (Exception)
        {
            // a cancelled or aborted request: the client went away
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // already closed
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Close();
#pragma warning disable VSTHRD003 // the accept loop is this server's own task
        await _loop;
#pragma warning restore VSTHRD003
        _stop.Dispose();
    }
}

/// <summary>
/// The media capture path in the app (M3.5): the capture service, the executor that runs a media job, start-up recovery and the
/// routing control, against the real repositories, the real loader and runner, and a local server.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-001")]
[Trait("TC", "TC-MED-005")]
public sealed class MediaCaptureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-media-app-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<EngineEvent> _events = new();
    private LocalStreamServer _host = null!;
    private CheckpointWriter _checkpoints = null!;
    private DownloadEngine _engine = null!;
    private JobRepository _jobs = null!;
    private JobSecretRepository _secrets = null!;
    private CategoryRepository _categories = null!;
    private CategoryRuleRepository _rules = null!;
    private MediaJobRunner _runner = null!;
    private MediaSourceLoader _loader = null!;
    private BandwidthLimiter _limiter = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_root, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        _checkpoints = new CheckpointWriter(factory);
        _checkpoints.Start();
        _jobs = new JobRepository(factory);
        _secrets = new JobSecretRepository(factory);
        _categories = new CategoryRepository(factory);
        _rules = new CategoryRuleRepository(factory);
        _limiter = new BandwidthLimiter();
        _engine = new DownloadEngine(_jobs, new SegmentRepository(factory), _checkpoints, _limiter);
        await _engine.StartAsync(ct);
        _engine.EventRaised += _events.Enqueue;
        _runner = new MediaJobRunner(new MediaRepository(factory), _jobs);
        _loader = new MediaSourceLoader();
        _host = new LocalStreamServer();
        AddStreams(_host);
    }

    public async ValueTask DisposeAsync()
    {
        _loader.Dispose();
        await _host.DisposeAsync();
        await _engine.DisposeAsync();
        await _checkpoints.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the folder is in the temp directory
        }
    }

    [Fact]
    public async Task A_protected_stream_is_refused_before_any_job_or_segment_exists()
    {
        var capture = Capture(new RecordingControl());

        var result = await capture.CaptureAsync(Message("/hls/sample-aes/master.m3u8", IpcMediaKind.Hls), TestContext.Current.CancellationToken);

        result.Status.Should().Be(MediaCaptureStatus.Protected);
        result.Detail.Should().Be("This video is protected and can't be saved");
        (await _jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEmpty();
        _host.Requests.Should().NotContain(path => path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_clear_stream_becomes_one_queued_media_job_in_its_category_folder_and_the_grid_hears_about_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new RecordingControl();
        var message = Message("/hls/clear/master.m3u8", IpcMediaKind.Hls) with
        {
            Cookies = [new IpcCookie { Name = "sid", Value = "s3cret", Domain = _host.BaseAddress.Host, Path = "/", Secure = false, HttpOnly = false }],
        };

        var result = await Capture(control).CaptureAsync(message, ct);

        result.Status.Should().Be(MediaCaptureStatus.Accepted);
        var job = (await _jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle().Which;
        job.Kind.Should().Be(JobKind.Media);
        job.State.Should().Be(JobState.Queued);
        job.Folder.Should().Be(Path.Combine(_root, "Downloads", "Video"), "the category rules place a video like any other download");
        job.CategoryId.Should().NotBeNull();
        control.Started.Should().Equal(job.Id);
        (await _secrets.GetAsync(job.Id, ct))!.Cookies.Should().ContainSingle(cookie => cookie.Name == "sid");
        await WaitUntilAsync(() => _events.OfType<JobAdded>().Any(added => added.Job.Id == job.Id));
    }

    [Fact]
    public async Task Capturing_the_same_stream_twice_is_one_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(new RecordingControl());

        var first = await capture.CaptureAsync(Message("/hls/clear/master.m3u8", IpcMediaKind.Hls), ct);
        var second = await capture.CaptureAsync(Message("/hls/clear/master.m3u8", IpcMediaKind.Hls), ct);

        first.Status.Should().Be(MediaCaptureStatus.Accepted);
        second.Status.Should().Be(MediaCaptureStatus.Accepted);
        (await _jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_start_that_fails_still_leaves_the_job_in_the_list_and_an_unreadable_link_is_a_clear_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new RecordingControl { FailStart = new InvalidOperationException("no slot") };

        var started = await Capture(control).CaptureAsync(Message("/hls/clear/master.m3u8", IpcMediaKind.Hls), ct);
        var unreadable = await Capture(new RecordingControl()).CaptureAsync(Message("/hls/no-such-kind/master.m3u8", IpcMediaKind.Hls), ct);

        started.Status.Should().Be(MediaCaptureStatus.Accepted);
        (await _jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle().Which.State.Should().Be(JobState.Queued);
        unreadable.Status.Should().Be(MediaCaptureStatus.Failed);
        unreadable.Detail.Should().Be("The media link could not be read.");
    }

    [Fact]
    public async Task The_routing_control_sends_media_jobs_to_the_executor_and_the_rest_to_the_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        var downloads = new RecordingControl();
        var media = new RecordingControl();
        var http = await AddJobAsync(JobKind.Http, JobState.Queued, "http.bin");
        var stream = await AddJobAsync(JobKind.Media, JobState.Queued, "stream.mp4");
        var routing = new RoutingJobControl(downloads, _jobs, media);

        await routing.StartJobAsync(http.Id, ct);
        await routing.StartJobAsync(stream.Id, ct);
        await routing.PauseJobAsync(stream.Id, ct);
        var missing = () => routing.StartJobAsync(JobId.New(TimeProvider.System), ct);

        downloads.Started.Should().Equal(http.Id);
        media.Started.Should().Equal(stream.Id);
        media.Paused.Should().Equal(stream.Id);
        downloads.Paused.Should().BeEmpty();
        await missing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Without_ffmpeg_a_media_job_fails_at_once_with_a_clear_message_and_reports_each_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AddJobAsync(JobKind.Media, JobState.Queued, "stream.mp4");
        var executor = Executor(ffmpeg: null);

        await executor.StartJobAsync(job.Id, ct);

        await WaitUntilAsync(async () => (await _jobs.GetAsync(job.Id, ct))!.State == JobState.Failed);
        (await _jobs.GetAsync(job.Id, ct))!.Error.Should().Be(MediaJobExecutor.FfmpegMissingMessage);
        await WaitUntilAsync(() => _events.OfType<JobStateChanged>().Any(change => change.Id == job.Id && change.To == JobState.Failed));
        _events.OfType<JobStateChanged>().Where(change => change.Id == job.Id).Select(change => (change.From, change.To))
            .Should().ContainInOrder(
                (JobState.Queued, JobState.Probing), (JobState.Probing, JobState.Downloading), (JobState.Downloading, JobState.Failed));
    }

    [Fact]
    public async Task Pausing_stops_the_download_and_parks_the_job_as_paused_and_it_can_be_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AddJobAsync(JobKind.Media, JobState.Queued, "stream.mp4", path: "/hls/stall/master.m3u8");
        var executor = Executor(new FfmpegOptions("ffmpeg-not-used.exe", "ffprobe-not-used.exe"));

        await executor.StartJobAsync(job.Id, ct);
        (await _jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Downloading);

        await executor.PauseJobAsync(job.Id, ct);

        var paused = (await _jobs.GetAsync(job.Id, ct))!;
        paused.State.Should().Be(JobState.Paused);
        paused.PauseReason.Should().Be(PauseReason.UserRequested);
        await WaitUntilAsync(() => _events.OfType<JobStateChanged>().Any(change => change.Id == job.Id && change.To == JobState.Paused));

        await executor.StartJobAsync(job.Id, ct);
        (await _jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Downloading);
        await executor.PauseJobAsync(job.Id, ct);
        (await _jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Paused);
    }

    [Fact]
    public async Task Recovery_requeues_a_media_job_caught_downloading_probing_or_verifying_and_completes_one_whose_file_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var downloading = await AddJobAsync(JobKind.Media, JobState.Downloading, "a.mp4");
        var probing = await AddJobAsync(JobKind.Media, JobState.Probing, "b.mp4");
        var verifyingLost = await AddJobAsync(JobKind.Media, JobState.Verifying, "c.mp4");
        var verifyingSaved = await AddJobAsync(JobKind.Media, JobState.Verifying, "d.mp4");
        var http = await AddJobAsync(JobKind.Http, JobState.Downloading, "e.bin");
        await File.WriteAllTextAsync(Path.Combine(verifyingSaved.Folder, verifyingSaved.FileName), "saved", ct);
        await _secrets.SetAsync(verifyingSaved.Id, new JobSecret { Cookies = [new JobCookie { Name = "sid", Value = "v", Domain = "example.com", Path = "/" }] }, ct);

        await Executor(ffmpeg: null).RecoverAsync(ct);

        (await _jobs.GetAsync(downloading.Id, ct))!.State.Should().Be(JobState.Queued);
        (await _jobs.GetAsync(probing.Id, ct))!.State.Should().Be(JobState.Queued);
        (await _jobs.GetAsync(verifyingLost.Id, ct))!.State.Should().Be(JobState.Queued);
        (await _jobs.GetAsync(verifyingSaved.Id, ct))!.State.Should().Be(JobState.Completed);
        (await _secrets.GetAsync(verifyingSaved.Id, ct)).Should().BeNull("the cookies go when the job completes");
        (await _jobs.GetAsync(http.Id, ct))!.State.Should().Be(JobState.Downloading, "the media executor leaves HTTP jobs to the engine");
        await WaitUntilAsync(() => _events.OfType<JobStateChanged>().Count(change => change.To == JobState.Queued) >= 3);
    }

    [Fact]
    public async Task A_job_caught_muxing_is_run_again_and_here_fails_clearly_without_ffmpeg()
    {
        var ct = TestContext.Current.CancellationToken;
        var muxing = await AddJobAsync(JobKind.Media, JobState.Muxing, "m.mp4");

        await Executor(ffmpeg: null).RecoverAsync(ct);

        await WaitUntilAsync(async () => (await _jobs.GetAsync(muxing.Id, ct))!.State == JobState.Failed);
        (await _jobs.GetAsync(muxing.Id, ct))!.Error.Should().Be(MediaJobExecutor.FfmpegMissingMessage);
    }

    [Fact]
    public async Task A_captured_stream_is_downloaded_muxed_and_completed_with_progress_and_its_cookies_removed()
    {
        if (!FfmpegLocator.TryFind(out var ffmpeg))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                throw new InvalidOperationException("FFmpeg/ffprobe are required in CI (KASI_FFMPEG/KASI_FFPROBE or PATH).");
            }

            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var executor = Executor(ffmpeg);
        var routing = new RoutingJobControl(new RecordingControl(), _jobs, executor);

        var result = await Capture(routing).CaptureAsync(Message("/hls/muxable/master.m3u8", IpcMediaKind.Hls), ct);

        result.Status.Should().Be(MediaCaptureStatus.Accepted);
        var added = (await _jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle().Which;
        await WaitUntilAsync(async () => (await _jobs.GetAsync(added.Id, ct))!.State == JobState.Completed, TimeSpan.FromSeconds(120));
        var done = (await _jobs.GetAsync(added.Id, ct))!;
        File.Exists(Path.Combine(done.Folder, done.FileName)).Should().BeTrue();
        (await _secrets.GetAsync(added.Id, ct)).Should().BeNull();
        await WaitUntilAsync(() => _events.OfType<JobStateChanged>().Any(change => change.Id == added.Id && change.To == JobState.Completed));
        _events.OfType<JobStateChanged>().Where(change => change.Id == added.Id).Select(change => change.To)
            .Should().ContainInOrder(JobState.Probing, JobState.Downloading, JobState.Muxing, JobState.Verifying, JobState.Completed);
        _events.OfType<JobProgressed>().Should().Contain(progress => progress.Id == added.Id && progress.DoneBytes > 0);
    }

    [Fact]
    public async Task The_picker_choice_is_carried_into_the_media_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var picker = new RecordingPicker();
        var control = new RecordingControl();
        var capture = Capture(control, picker);
        var presentation = await LoadPresentationAsync("/hls/clear/master.m3u8", ct);
        picker.Selection = new MediaPickerSelection(
            presentation.Variants.Single(variant => variant.Height == 360),
            null,
            MediaPickerFormat.Matroska);

        var result = await capture.CaptureAsync(Message("/hls/clear/master.m3u8", IpcMediaKind.Hls), ct);

        result.Status.Should().Be(MediaCaptureStatus.Accepted);
        var job = (await _jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle().Which;
        job.FileName.Should().EndWith(".mkv");
        control.Started.Should().ContainSingle().Which.Should().Be(job.Id);
        var secret = await _secrets.GetAsync(job.Id, ct);
        secret!.Headers["X-Kasi-Media-Variant"].Should().Be(presentation.Variants.Single(variant => variant.Height == 360).Video.Id);
        secret.Headers["X-Kasi-Media-Format"].Should().Be("Matroska");
        picker.Title.Should().NotBeNullOrWhiteSpace();
    }

    private async Task<MediaPresentation> LoadPresentationAsync(string path, CancellationToken ct)
    {
        var message = Message(path, IpcMediaKind.Hls);
        var loaded = await _loader.LoadAsync(Kasi.Media.MediaKind.Hls, new Uri(message.Url), cancellationToken: ct);
        return ((MediaSourceLoader.Playable)loaded).Presentation;
    }

    [Fact]
    public void The_catalogue_keeps_the_newest_streams_and_forgets_the_least_recently_used_tabs()
    {
        var catalogue = new BrowserMediaCatalog();
        for (var i = 0; i < BrowserMediaCatalog.MaxPerTab + 5; i++)
        {
            catalogue.Add(Detected(1, $"https://example.com/{i}.m3u8"));
        }

        catalogue.ForTab(1).Should().HaveCount(BrowserMediaCatalog.MaxPerTab);
        catalogue.ForTab(1)[0].Url.Should().Be("https://example.com/5.m3u8", "the oldest streams went first");
        catalogue.ForTab(1)[^1].Url.Should().Be($"https://example.com/{BrowserMediaCatalog.MaxPerTab + 4}.m3u8");

        for (var tab = 2; tab <= BrowserMediaCatalog.MaxTabs + 1; tab++)
        {
            catalogue.Add(Detected(tab, "https://example.com/x.m3u8"));
        }

        catalogue.ForTab(1).Should().BeEmpty("tab 1 was the least recently touched when the 257th tab arrived");
        catalogue.All().Should().HaveCount(BrowserMediaCatalog.MaxTabs);
    }

    private static void AddStreams(LocalStreamServer server)
    {
        const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=200000,RESOLUTION=160x90,CODECS=\"avc1.42c00c,mp4a.40.2\"\nv.m3u8\n";
        const string Clear = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nseg0.ts\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n";
        const string Protected = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\nseg0.ts\n#EXT-X-ENDLIST\n";
        server.Text("/hls/clear/master.m3u8", Master);
        server.Text("/hls/clear/v.m3u8", Clear);
        server.Text("/hls/sample-aes/master.m3u8", Master);
        server.Text("/hls/sample-aes/v.m3u8", Protected);
        server.Route("/hls/stall/master.m3u8", async (_, ct) => await Task.Delay(TimeSpan.FromMinutes(5), ct)); // answers only when the test pauses the job

        var fixtures = Path.Combine(RepositoryRoot(), "tests", "fixtures", "media", "muxable", "hls");
        server.File("/hls/muxable/master.m3u8", Path.Combine(fixtures, "master.m3u8"));
        server.File("/hls/muxable/index.m3u8", Path.Combine(fixtures, "index.m3u8"));
        foreach (var segment in Directory.GetFiles(fixtures, "segment-*.ts"))
        {
            server.File("/hls/muxable/" + Path.GetFileName(segment), segment);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kasi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kasi.slnx was not found.");
    }

    private static MediaDetected Detected(int tab, string url) =>
        new() { Id = "m" + tab, TabId = tab, PageUrl = "https://example.com/", Kind = IpcMediaKind.Hls, Url = url };

    private BrowserMediaCapture Capture(IJobControl control, IMediaPicker? picker = null) => new(
        _loader,
        _jobs,
        _secrets,
        control,
        jobService: null!, // only direct media uses it
        _categories,
        _rules,
        _engine.PublishExternal,
        Path.Combine(_root, "Downloads"),
        picker: picker);

    private MediaJobExecutor Executor(FfmpegOptions? ffmpeg) => new(
        _jobs, _secrets, _runner, _loader, _limiter, Path.Combine(_root, "media"), ffmpeg, _engine.PublishExternal);

    private CaptureMedia Message(string path, IpcMediaKind kind) => new()
    {
        Id = "cm-" + Guid.NewGuid().ToString("N")[..6],
        TabId = 1,
        Url = new Uri(_host.BaseAddress, path).AbsoluteUri,
        Kind = kind,
        Cookies = [],
        Referrer = "https://page.example/watch",
        UserAgent = "Kasi-Test/1.0",
    };

    private async Task<Job> AddJobAsync(JobKind kind, JobState state, string fileName, string? path = null)
    {
        var folder = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(folder);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = kind,
            Url = new Uri(_host.BaseAddress, path ?? "/hls/clear/master.m3u8").AbsoluteUri,
            FileName = fileName,
            Folder = folder,
            QueueId = 1,
            QueuePos = 1,
            State = state,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _jobs.AddAsync(job, TestContext.Current.CancellationToken);
        return job;
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
