using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Tray;
using Kasi.App.ViewModels;
using Kasi.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

/// <summary>A service whose events the test raises and whose jobs it chooses.</summary>
internal sealed class EventingJobService : IJobService
{
    private readonly RecordingJobService _inner = new();

    public event Action<EngineEvent>? EventRaised;

    public Dictionary<JobId, Job> Stored => _inner.Stored;

    public int Listeners => EventRaised?.GetInvocationList().Length ?? 0;

    public RecordingJobService Inner => _inner;

    public void Raise(EngineEvent e) => EventRaised?.Invoke(e);

    public long? SpeedLimit
    {
        get => _inner.SpeedLimit;
        set => _inner.SpeedLimit = value;
    }

    public Task PauseAllAsync(CancellationToken cancellationToken = default) => _inner.PauseAllAsync(cancellationToken);

    public Task ResumeAllAsync(CancellationToken cancellationToken = default) => _inner.ResumeAllAsync(cancellationToken);

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => _inner.CategoriesAsync(cancellationToken);

    public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => _inner.QueuesAsync(cancellationToken);

    public Task<AddResult> AddAsync(NewDownload request, Kasi.Engine.DuplicateAction? resolution = null, CancellationToken cancellationToken = default) => _inner.AddAsync(request, resolution, cancellationToken);

    public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => _inner.GetAsync(id, cancellationToken);

    public Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default) => _inner.RefreshLinkAsync(id, url, cookies, cancellationToken);

    public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => _inner.PauseAsync(id, cancellationToken);

    public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => _inner.ResumeAsync(id, cancellationToken);

    public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) => _inner.CancelAsync(id, deleteFiles, cancellationToken);
}

[Trait("Requirement", "KASI-UI-004")]
public class TrayIconViewModelTests
{
    private sealed class FakeApp : IAppLifetime
    {
        public int Shown { get; private set; }

        public int Quits { get; private set; }

        public void ShowMainWindow() => Shown++;

        public void Quit() => Quits++;
    }

    private static (TrayIconViewModel Tray, EventingJobService Jobs, FakeApp App, FakeTimeProvider Clock) Make()
    {
        var jobs = new EventingJobService();
        var app = new FakeApp();
        var clock = new FakeTimeProvider();
        var tray = new TrayIconViewModel(jobs, app, action => action(), clock);
        tray.Start();
        return (tray, jobs, app, clock);
    }

    [Fact]
    [Trait("TC", "TC-UI-003")]
    public async Task The_menu_actions_work_with_no_window_open()
    {
        var (tray, jobs, app, _) = Make();

        await tray.PauseAllCommand.ExecuteAsync(null);
        await tray.ResumeAllCommand.ExecuteAsync(null);
        await tray.ResumeAllCommand.ExecuteAsync(null);
        tray.OpenCommand.Execute(null);
        tray.QuitCommand.Execute(null);

        jobs.Inner.PauseAlls.Should().Be(1);
        jobs.Inner.ResumeAlls.Should().Be(2);
        app.Shown.Should().Be(1);
        app.Quits.Should().Be(1);
        await tray.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-UI-003")]
    public async Task A_speed_preset_sets_the_shared_limit_and_unlimited_clears_it()
    {
        var (tray, jobs, _, _) = Make();
        var tenMb = tray.SpeedPresets.Single(p => p.BytesPerSecond == 10 * 1024 * 1024);

        tray.SetSpeedLimitCommand.Execute(tenMb);
        jobs.SpeedLimit.Should().Be(10 * 1024 * 1024);
        tray.SelectedPreset.Should().Be(tenMb);
        tray.SetSpeedLimitCommand.Execute(tray.SpeedPresets[0]);

        jobs.SpeedLimit.Should().BeNull();
        tray.SpeedPresets[0].BytesPerSecond.Should().BeNull();
        await tray.DisposeAsync();
    }

    [Fact]
    public async Task It_starts_with_the_preset_that_matches_the_current_limit()
    {
        var jobs = new EventingJobService { SpeedLimit = 5 * 1024 * 1024 };

        var tray = new TrayIconViewModel(jobs, new FakeApp(), action => action());

        tray.SelectedPreset.BytesPerSecond.Should().Be(5 * 1024 * 1024);
        await tray.DisposeAsync();
    }

    [Fact]
    public async Task The_tooltip_shows_the_total_speed_once_per_tick_and_goes_back_to_the_name_when_nothing_runs()
    {
        var (tray, jobs, _, clock) = Make();
        var a = JobId.New(TimeProvider.System);
        var b = JobId.New(TimeProvider.System);

        jobs.Raise(new JobProgressed(a, 1, 100, 1024 * 1024));
        jobs.Raise(new JobProgressed(b, 1, 100, 2 * 1024 * 1024));
        tray.ToolTip.Should().Be("Kasi", "nothing is applied before the tick");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => tray.ToolTip != "Kasi");
        tray.ToolTip.Should().Be("Kasi - 3 MB/s");

        jobs.Raise(new JobStateChanged(a, JobState.Downloading, JobState.Paused, null));
        jobs.Raise(new JobRemoved(b));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => tray.ToolTip == "Kasi");
        await tray.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_stops_listening()
    {
        var (tray, jobs, _, _) = Make();

        await tray.DisposeAsync();

        jobs.Listeners.Should().Be(0);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(3L * 1024 * 1024, "3 MB")]
    [InlineData(-5L, "0 B")]
    public void Sizes_read_the_way_people_write_them(long bytes, string expected) =>
        ByteText.Format(bytes).Should().Be(expected);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30)); // generous: the apply runs on the dispatcher's own loop
        while (!condition())
        {
            await Task.Delay(10, limit.Token);
        }
    }
}

[Trait("Requirement", "KASI-UI-005")]
public class NotificationTests
{
    private sealed class RecordingShell : IShell
    {
        public List<string> Opened { get; } = [];

        public List<string> Shown { get; } = [];

        public void OpenFile(string path) => Opened.Add(path);

        public void ShowInFolder(string path) => Shown.Add(path);
    }

    private sealed class RecordingToasts : INotificationService
    {
        public List<(string Kind, JobId Id, string Name)> Shown { get; } = [];

        public void ShowCompleted(JobId id, string fileName)
        {
            lock (Shown)
            {
                Shown.Add(("completed", id, fileName));
            }
        }

        public void ShowFailed(JobId id, string fileName)
        {
            lock (Shown)
            {
                Shown.Add(("failed", id, fileName));
            }
        }
    }

    private static Job Finished(JobState state = JobState.Completed) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = "https://files.example.org/pack.zip?token=SECRET",
        FileName = "pack.zip",
        Folder = @"C:\Downloads\Compressed",
        QueueId = 1,
        QueuePos = 1,
        State = state,
        Error = "connection to files.example.org failed",
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30)); // generous: the notifier works on the thread pool
        while (!condition())
        {
            await Task.Delay(10, limit.Token);
        }
    }

    [Fact]
    public async Task Clicking_open_opens_the_stored_file_and_clicking_show_in_folder_selects_it()
    {
        var jobs = new RecordingJobService();
        var job = Finished();
        jobs.Stored[job.Id] = job;
        var shell = new RecordingShell();
        var router = new NotificationRouter(jobs, shell);

        (await router.HandleAsync($"action=open-file&job={job.Id}", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await router.HandleAsync($"action=open-folder&job={job.Id}", TestContext.Current.CancellationToken)).Should().BeTrue();

        shell.Opened.Should().Equal(@"C:\Downloads\Compressed\pack.zip");
        shell.Shown.Should().Equal(@"C:\Downloads\Compressed\pack.zip");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("action=open-file")]
    [InlineData("job=not-a-guid&action=open-file")]
    [InlineData("action=format-disk&job=00000000-0000-0000-0000-000000000001")]
    [InlineData("action=open-file&job=00000000-0000-0000-0000-000000000001&path=C%3A%5CWindows%5Cnotepad.exe")]
    public async Task Arguments_that_are_not_ours_do_nothing(string? arguments)
    {
        var shell = new RecordingShell();
        var router = new NotificationRouter(new RecordingJobService(), shell);

        (await router.HandleAsync(arguments, TestContext.Current.CancellationToken)).Should().BeFalse();

        shell.Opened.Should().BeEmpty();
        shell.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task A_path_in_the_arguments_is_never_used_and_a_job_that_is_not_finished_is_never_opened()
    {
        var jobs = new RecordingJobService();
        var job = Finished();
        var running = Finished(JobState.Downloading);
        jobs.Stored[job.Id] = job;
        jobs.Stored[running.Id] = running;
        var shell = new RecordingShell();
        var router = new NotificationRouter(jobs, shell);

        await router.HandleAsync($"action=open-file&job={job.Id}&path=C%3A%5CWindows%5Cnotepad.exe", TestContext.Current.CancellationToken);
        (await router.HandleAsync($"action=open-file&job={running.Id}", TestContext.Current.CancellationToken)).Should().BeFalse();

        shell.Opened.Should().Equal(@"C:\Downloads\Compressed\pack.zip");
    }

    [Fact]
    public async Task A_finished_download_raises_a_toast_with_the_file_name_and_nothing_else()
    {
        var jobs = new EventingJobService();
        var job = Finished();
        jobs.Stored[job.Id] = job;
        var toasts = new RecordingToasts();
        new CompletionNotifier(jobs, toasts).Watch();

        jobs.Raise(new JobStateChanged(job.Id, JobState.Verifying, JobState.Completed, null));
        await WaitUntilAsync(() => toasts.Shown.Count == 1);

        toasts.Shown.Single().Should().Be(("completed", job.Id, "pack.zip"));
    }

    [Fact]
    public async Task A_failed_download_raises_a_failure_toast_without_the_error_text()
    {
        var jobs = new EventingJobService();
        var job = Finished(JobState.Failed);
        jobs.Stored[job.Id] = job;
        var toasts = new RecordingToasts();
        new CompletionNotifier(jobs, toasts).Watch();

        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Failed, "connection to files.example.org failed"));
        await WaitUntilAsync(() => toasts.Shown.Count == 1);

        toasts.Shown.Single().Kind.Should().Be("failed");
        toasts.Shown.Single().Name.Should().Be("pack.zip").And.NotContain("example.org");
    }

    [Fact]
    public async Task Other_state_changes_and_vanished_jobs_raise_nothing()
    {
        var jobs = new EventingJobService();
        var toasts = new RecordingToasts();
        var faults = new List<Exception>();
        new CompletionNotifier(jobs, toasts, faults.Add).Watch();

        jobs.Raise(new JobStateChanged(JobId.New(TimeProvider.System), JobState.Queued, JobState.Downloading, null));
        jobs.Raise(new JobStateChanged(JobId.New(TimeProvider.System), JobState.Downloading, JobState.Completed, null));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        toasts.Shown.Should().BeEmpty();
        faults.Should().BeEmpty();
    }
}
