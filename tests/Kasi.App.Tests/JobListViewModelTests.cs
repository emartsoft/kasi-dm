using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Engine;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-UI-001")]
public class JobListViewModelTests
{
    private sealed class FakeJobService(params Job[] stored) : IJobService
    {
        public event Action<EngineEvent>? EventRaised;

        public int Listeners => EventRaised?.GetInvocationList().Length ?? 0;

        public void Raise(EngineEvent engineEvent) => EventRaised?.Invoke(engineEvent);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Job>>(stored);

        public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public long? SpeedLimit { get; set; }

        public Task PauseAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Job NewJob(string name, long? total = 1000, long done = 0) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = "https://files.example.org/" + name + "?token=SECRET-TOKEN",
        FileName = name,
        Folder = @"C:\Downloads",
        QueueId = 1,
        QueuePos = 1,
        TotalBytes = total,
        DoneBytes = done,
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static async Task<(JobListViewModel List, FakeJobService Service, FakeTimeProvider Clock)> StartAsync(params Job[] stored)
    {
        var service = new FakeJobService(stored);
        var clock = new FakeTimeProvider();
        var list = new JobListViewModel(service, action => action(), clock);
        await list.StartAsync(TestContext.Current.CancellationToken);
        return (list, service, clock);
    }

    [Fact]
    public async Task It_starts_with_the_stored_jobs_and_shows_names_not_urls()
    {
        var (list, _, _) = await StartAsync(NewJob("a.zip"), NewJob("b.iso", done: 500));

        list.Rows.Select(r => r.FileName).Should().Equal("a.zip", "b.iso");
        list.Rows[1].Percent.Should().Be(50);
        list.Rows.Select(r => r.ToString()).Should().NotContain(t => t!.Contains("SECRET-TOKEN"));
        await list.DisposeAsync();
    }

    [Fact]
    public async Task A_job_added_later_gets_a_row_once_even_if_the_load_saw_it_too()
    {
        var existing = NewJob("a.zip");
        var (list, service, _) = await StartAsync(existing);

        service.Raise(new JobAdded(existing));
        service.Raise(new JobAdded(NewJob("c.zip")));

        list.Rows.Select(r => r.FileName).Should().Equal("a.zip", "c.zip");
        await list.DisposeAsync();
    }

    [Fact]
    public async Task State_changes_apply_at_once_and_removal_drops_the_row()
    {
        var job = NewJob("a.zip");
        var (list, service, _) = await StartAsync(job);

        service.Raise(new JobStateChanged(job.Id, JobState.Queued, JobState.Downloading, null));
        list.Rows[0].State.Should().Be("Downloading");
        service.Raise(new JobRemoved(job.Id));

        list.Rows.Should().BeEmpty();
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Progress_waits_for_the_tick_and_only_the_latest_per_job_counts()
    {
        var job = NewJob("a.zip");
        var (list, service, clock) = await StartAsync(job);

        for (var i = 1; i <= 1000; i++)
        {
            service.Raise(new JobProgressed(job.Id, i, 1000, 100));
        }

        list.Rows[0].Percent.Should().Be(0, "nothing is applied before the tick");
        clock.Advance(JobListViewModel.ProgressPeriod);
        await WaitForAsync(() => list.Rows[0].Percent > 0);
        list.Rows[0].Percent.Should().Be(100);
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Progress_for_a_job_the_list_does_not_know_is_ignored()
    {
        var (list, service, clock) = await StartAsync(NewJob("a.zip"));

        service.Raise(new JobProgressed(JobId.New(TimeProvider.System), 5, 10, 1));
        clock.Advance(JobListViewModel.ProgressPeriod);

        list.Rows.Should().ContainSingle();
        await list.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_stops_listening_to_the_engine()
    {
        var (list, service, _) = await StartAsync(NewJob("a.zip"));

        await list.DisposeAsync();

        service.Listeners.Should().Be(0);
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
