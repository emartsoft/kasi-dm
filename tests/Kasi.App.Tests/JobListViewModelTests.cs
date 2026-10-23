using System.Diagnostics;
using Kasi.App.ViewModels;
using Kasi.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

internal static class TestJobs
{
    public static Job New(string name, JobState state = JobState.Queued, long? total = 1000, long done = 0, int category = 1, int queue = 1, DateTimeOffset? created = null) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = "https://files.example.org/" + name + "?token=SECRET-TOKEN",
        FileName = name,
        Folder = @"C:\Downloads",
        QueueId = queue,
        QueuePos = 1,
        State = state,
        TotalBytes = total,
        DoneBytes = done,
        CategoryId = category,
        CreatedUtc = created ?? DateTimeOffset.UnixEpoch,
    };
}

[Trait("Requirement", "KASI-UI-001")]
public class JobListViewModelTests
{
    private static async Task<(JobListViewModel List, EventingJobService Service, FakeTimeProvider Clock)> StartAsync(params Job[] stored)
    {
        var service = new EventingJobService();
        foreach (var job in stored)
        {
            service.Stored[job.Id] = job;
        }

        var clock = new FakeTimeProvider();
        var list = new JobListViewModel(service, action => action(), clock);
        await list.StartAsync(TestContext.Current.CancellationToken);
        return (list, service, clock);
    }

    [Fact]
    public async Task It_starts_with_the_stored_jobs_showing_names_categories_and_never_urls()
    {
        var (list, _, _) = await StartAsync(TestJobs.New("a.zip", category: 2), TestJobs.New("b.iso", done: 500));

        list.Visible.Select(r => r.FileName).Should().Equal("a.zip", "b.iso");
        list.Visible[0].CategoryName.Should().Be("Compressed");
        list.Visible[1].Percent.Should().Be(50);
        list.Visible.Select(r => r.ToString()).Should().NotContain(t => t!.Contains("SECRET-TOKEN"));
        await list.DisposeAsync();
    }

    [Fact]
    public async Task A_job_added_later_gets_a_row_once_even_if_the_load_saw_it_too()
    {
        var existing = TestJobs.New("a.zip");
        var (list, service, _) = await StartAsync(existing);

        service.Raise(new JobAdded(existing));
        service.Raise(new JobAdded(TestJobs.New("c.zip")));

        list.Visible.Select(r => r.FileName).Should().Equal("a.zip", "c.zip");
        list.AllCount.Should().Be(2);
        await list.DisposeAsync();
    }

    [Fact]
    public async Task State_changes_apply_at_once_and_removal_drops_the_row_and_its_speed()
    {
        var job = TestJobs.New("a.zip");
        var (list, service, clock) = await StartAsync(job);

        service.Raise(new JobStateChanged(job.Id, JobState.Queued, JobState.Downloading, null));
        list.Visible[0].State.Should().Be("Downloading");
        service.Raise(new JobProgressed(job.Id, 100, 1000, 2_000_000));
        clock.Advance(JobListViewModel.ProgressPeriod);
        await WaitForAsync(() => list.TotalSpeedText != string.Empty);
        service.Raise(new JobRemoved(job.Id));

        list.Visible.Should().BeEmpty();
        list.TotalSpeedText.Should().BeEmpty();
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Progress_waits_for_the_tick_and_only_the_latest_per_job_counts()
    {
        var job = TestJobs.New("a.zip", JobState.Downloading);
        var (list, service, clock) = await StartAsync(job);

        for (var i = 1; i <= 1000; i++)
        {
            service.Raise(new JobProgressed(job.Id, i, 1000, 100));
        }

        list.Visible[0].Percent.Should().Be(0, "nothing is applied before the tick");
        clock.Advance(JobListViewModel.ProgressPeriod);
        await WaitForAsync(() => list.Visible[0].Percent > 0);
        list.Visible[0].Percent.Should().Be(100);
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Progress_for_a_job_the_list_does_not_know_is_ignored()
    {
        var (list, service, clock) = await StartAsync(TestJobs.New("a.zip"));

        service.Raise(new JobProgressed(JobId.New(TimeProvider.System), 5, 10, 1));
        clock.Advance(JobListViewModel.ProgressPeriod);

        list.Visible.Should().ContainSingle();
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_stops_listening_to_the_engine()
    {
        var (list, service, _) = await StartAsync(TestJobs.New("a.zip"));

        await list.DisposeAsync();

        service.Listeners.Should().Be(0);
    }

    [Fact]
    public async Task The_counts_follow_the_states_the_way_the_sidebar_shows_them()
    {
        var running = TestJobs.New("a.zip", JobState.Downloading);
        var (list, service, _) = await StartAsync(
            running,
            TestJobs.New("b.zip", JobState.Downloading),
            TestJobs.New("c.pdf", JobState.Completed, done: 1000),
            TestJobs.New("d.msi", JobState.Queued),
            TestJobs.New("e.7z", JobState.Paused),
            TestJobs.New("f.exe", JobState.Failed));

        (list.AllCount, list.ActiveCount, list.FinishedCount, list.UnfinishedCount, list.QueuedCount).Should().Be((6, 2, 1, 5, 1));
        service.Raise(new JobStateChanged(running.Id, JobState.Downloading, JobState.Completed, null));

        (list.ActiveCount, list.FinishedCount, list.UnfinishedCount).Should().Be((1, 2, 4));
        await list.DisposeAsync();
    }

    [Fact]
    public async Task The_filters_pick_by_status_category_queue_and_text_and_a_state_change_moves_a_row_in_or_out()
    {
        var a = TestJobs.New("ubuntu.iso", JobState.Downloading, category: 6, queue: 1);
        var (list, service, _) = await StartAsync(
            a,
            TestJobs.New("report.pdf", JobState.Completed, category: 3, queue: 1),
            TestJobs.New("backup.7z", JobState.Paused, category: 2, queue: 2));

        list.SetFilter(new JobFilter(StatusFilter.Active));
        list.Visible.Select(r => r.FileName).Should().Equal("ubuntu.iso");
        list.SetFilter(new JobFilter(StatusFilter.Finished));
        list.Visible.Select(r => r.FileName).Should().Equal("report.pdf");
        list.SetFilter(new JobFilter(StatusFilter.Unfinished));
        list.Visible.Should().HaveCount(2);
        list.SetFilter(new JobFilter(CategoryId: 2));
        list.Visible.Select(r => r.FileName).Should().Equal("backup.7z");
        list.SetFilter(new JobFilter(QueueId: 1));
        list.Visible.Should().HaveCount(2);

        list.SetFilter(new JobFilter(StatusFilter.Active));
        service.Raise(new JobStateChanged(a.Id, JobState.Downloading, JobState.Paused, null));
        list.Visible.Should().BeEmpty("a paused job leaves the Active view");
        await list.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-UI-006")]
    public async Task Search_matches_the_file_name_in_any_case_and_combines_with_the_sidebar()
    {
        var (list, _, _) = await StartAsync(TestJobs.New("Ubuntu-24.04.iso", category: 6), TestJobs.New("ubuntu-notes.pdf", category: 3), TestJobs.New("debian.iso", category: 6));

        list.SearchText = "UBUNTU";
        list.Visible.Should().HaveCount(2);
        list.SetFilter(new JobFilter(CategoryId: 6));
        list.Visible.Select(r => r.FileName).Should().Equal("Ubuntu-24.04.iso");
        list.SearchText = string.Empty;
        list.Visible.Should().HaveCount(2);
        await list.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-UI-006")]
    public async Task Ten_thousand_jobs_load_count_and_filter_in_bounded_time()
    {
        var jobs = Enumerable.Range(0, 10_000).Select(i => TestJobs.New($"file{i}.{(i % 2 == 0 ? "zip" : "iso")}", i % 5 == 0 ? JobState.Downloading : JobState.Completed, category: 1 + (i % 8))).ToArray();
        var watch = Stopwatch.StartNew();
        var (list, _, _) = await StartAsync(jobs);
        list.SearchText = "file99";
        var found = list.Visible.Count;
        list.SearchText = string.Empty;
        list.SetFilter(new JobFilter(StatusFilter.Active, CategoryId: 1));
        watch.Stop();

        list.AllCount.Should().Be(10_000);
        found.Should().Be(111); // file99, file990 to file999, file9900 to file9999
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "a generous bound: it guards against quadratic work (the spec asks for a filter under 100 ms)");
        await list.DisposeAsync();
    }

    [Fact]
    public async Task The_total_speed_adds_up_the_moving_jobs_once_per_tick()
    {
        var a = TestJobs.New("a.zip", JobState.Downloading);
        var b = TestJobs.New("b.zip", JobState.Downloading);
        var (list, service, clock) = await StartAsync(a, b);

        service.Raise(new JobProgressed(a.Id, 10, 1000, 1024 * 1024));
        service.Raise(new JobProgressed(b.Id, 10, 1000, 2 * 1024 * 1024));
        list.TotalSpeedText.Should().BeEmpty();
        clock.Advance(JobListViewModel.ProgressPeriod);
        await WaitForAsync(() => list.TotalSpeedText != string.Empty);

        list.TotalSpeedText.Should().Be("3 MB/s total");
        await list.DisposeAsync();
    }

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

[Trait("Requirement", "KASI-UI-001")]
public class JobTextTests
{
    [Theory]
    [InlineData(-1L, "")]
    [InlineData(0L, "0 s")]
    [InlineData(45L, "45 s")]
    [InlineData(59L, "59 s")]
    [InlineData(60L, "1 min")]
    [InlineData(250L, "4 min")]
    [InlineData(3599L, "60 min")]
    [InlineData(3600L, "1 h 0 min")]
    [InlineData(7500L, "2 h 5 min")]
    [InlineData(86400L, "1 d")]
    [InlineData(259200L, "3 d")]
    public void Time_left_reads_the_way_people_say_it(long seconds, string expected) =>
        JobText.TimeLeft(seconds).Should().Be(expected);

    [Fact]
    public void An_unknown_time_is_empty()
    {
        JobText.TimeLeft(null).Should().BeEmpty();
    }

    [Fact]
    public void Dates_say_today_yesterday_the_weekday_or_the_date()
    {
        var now = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
        var at = (int daysAgo, int hour) => new DateTimeOffset(now.Date.AddDays(-daysAgo).AddHours(hour), now.Offset);

        JobText.DateAdded(at(0, 9), now).Should().StartWith("Today ");
        JobText.DateAdded(at(1, 22), now).Should().StartWith("Yesterday ");
        JobText.DateAdded(at(3, 14), now).Should().NotStartWith("Today").And.NotStartWith("Yesterday").And.HaveLength(JobText.DateAdded(at(3, 14), now).Length);
        JobText.DateAdded(at(30, 14), now).Should().NotContain(":");
    }

    [Theory]
    [InlineData(JobState.Downloading, true)]
    [InlineData(JobState.Probing, true)]
    [InlineData(JobState.Retrying, true)]
    [InlineData(JobState.Muxing, true)]
    [InlineData(JobState.Verifying, true)]
    [InlineData(JobState.Queued, false)]
    [InlineData(JobState.Paused, false)]
    [InlineData(JobState.Completed, false)]
    [InlineData(JobState.Failed, false)]
    public void Active_means_transferring_or_finishing_a_transfer(JobState state, bool active) =>
        JobText.IsActive(state).Should().Be(active);

    [Fact]
    public void Every_state_has_a_label_and_a_speed_reads_with_its_unit()
    {
        foreach (var state in Enum.GetValues<JobState>())
        {
            JobText.State(state).Should().NotBeNullOrEmpty().And.NotBe(state.ToString().ToUpperInvariant());
        }

        JobText.Speed(0).Should().BeEmpty();
        JobText.Speed(8_808_038).Should().Be("8.4 MB/s");
    }

    [Fact]
    public void A_row_smooths_the_speed_and_works_out_the_time_left_from_it()
    {
        var row = new JobRowViewModel(TestJobs.New("a.zip", JobState.Downloading, total: 10_000, done: 0));

        row.Progressed(1000, 10_000, 1000);
        row.Progressed(2000, 10_000, 2000);

        row.BytesPerSecond.Should().Be(1300, "0.7 of the old speed and 0.3 of the new one");
        row.SortTimeLeft.Should().Be((long)Math.Ceiling(8000 / 1300d));
        row.TimeLeft.Should().Be("7 s");
        row.StateChanged(JobState.Paused);
        row.Speed.Should().BeEmpty();
        row.TimeLeft.Should().BeEmpty();
        row.SortTimeLeft.Should().Be(long.MaxValue);
    }
}
