using Kasi.Core;
using Kasi.Data;
using Kasi.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Scheduling.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-QUE-007")]
[Trait("TC", "TC-QUE-006")]
public sealed class QueueRunnerQuotaTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private QueueRepository _queues = null!;
    private JobRepository _jobs = null!;
    private QueueUsageRepository _usage = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _queues = new QueueRepository(_db.Factory);
        _jobs = new JobRepository(_db.Factory);
        _usage = new QueueUsageRepository(_db.Factory);
        var queue = await _queues.GetAsync(1, TempDatabase.Ct);
        await _queues.UpdateAsync(queue! with { QuotaBytes = 1_000, QuotaReset = QuotaReset.Daily, MaxParallel = 4 }, TempDatabase.Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_quota_stops_the_pump_before_the_second_job_and_pauses_the_running_one_when_the_cap_is_reported()
    {
        var first = await AddJobAsync(600, 1);
        var second = await AddJobAsync(600, 2);
        var configured = (await _queues.GetAsync(1, TempDatabase.Ct))!;
        configured.QuotaBytes.Should().Be(1_000);
        using var quota = new QueueQuotaService(_queues, _jobs, _usage, _clock);
        (await quota.TryReserveAsync(first, configured, TempDatabase.Ct)).Should().BeTrue();
        (await quota.TryReserveAsync(second, configured, TempDatabase.Ct)).Should().BeFalse();
        await quota.ReleaseAsync(first.Id, TempDatabase.Ct);
        var control = new CountingControl();
        using var runner = new QueueRunner(_queues, _jobs, control, quota);

        await runner.StartQueueAsync(1, TempDatabase.Ct);

        control.Started.Should().ContainSingle().Which.Should().Be(first.Id);
        runner.IsQueueRunning(1).Should().BeFalse();

        quota.Report(first.Id, 1_000);
        await WaitForAsync(() => control.Paused.Count == 1);
        control.Paused.Should().Contain(first.Id);
        (await runner.TryResumeAfterQuotaAsync(1, TempDatabase.Ct)).Should().BeFalse();
        control.Started.Should().NotContain(second.Id);
    }

    [Fact]
    public async Task A_new_period_requeues_the_quota_paused_job_and_starts_the_queue()
    {
        var job = await AddJobAsync(1_000, 1);
        using var quota = new QueueQuotaService(_queues, _jobs, _usage, _clock);
        var control = new CountingControl();
        using var runner = new QueueRunner(_queues, _jobs, control, quota);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Queued, JobState.Paused, pauseReason: PauseReason.Quota, cancellationToken: TempDatabase.Ct);

        _clock.Advance(TimeSpan.FromDays(1));

        (await runner.TryResumeAfterQuotaAsync(1, TempDatabase.Ct)).Should().BeTrue();
        control.Started.Should().Contain(job.Id);
        (await _jobs.GetAsync(job.Id, TempDatabase.Ct))!.State.Should().Be(JobState.Queued);
    }

    private async Task<Job> AddJobAsync(long total, double position)
    {
        var job = new Job
        {
            Id = JobId.New(_clock),
            Kind = JobKind.Http,
            Url = "https://example.test/file",
            FileName = "file.bin",
            Folder = Path.GetTempPath(),
            QueueId = 1,
            QueuePos = position,
            TotalBytes = total,
            CreatedUtc = _clock.GetUtcNow(),
        };
        await _jobs.AddAsync(job, TempDatabase.Ct);
        return job;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("quota condition was not met");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CountingControl : IJobControl
    {
        public List<JobId> Started { get; } = [];

        public List<JobId> Paused { get; } = [];

        public Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            Started.Add(id);
            return Task.CompletedTask;
        }

        public Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            Paused.Add(id);
            return Task.CompletedTask;
        }
    }
}
