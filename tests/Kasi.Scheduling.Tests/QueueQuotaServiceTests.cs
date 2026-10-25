using Kasi.Core;
using Kasi.Data;
using Kasi.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Scheduling.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-QUE-007")]
[Trait("TC", "TC-QUE-006")]
public sealed class QueueQuotaServiceTests : IAsyncLifetime, IDisposable
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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    private async Task<Queue> QuotaQueueAsync(long bytes, QuotaReset reset = QuotaReset.Daily)
    {
        var queue = await _queues.GetAsync(1, TempDatabase.Ct);
        await _queues.UpdateAsync(queue! with { QuotaBytes = bytes, QuotaReset = reset, MaxParallel = 4 }, TempDatabase.Ct);
        return (await _queues.GetAsync(1, TempDatabase.Ct))!;
    }

    private async Task<Job> JobAsync(long total, long done = 0)
    {
        var job = new Job
        {
            Id = JobId.New(_clock),
            Kind = JobKind.Http,
            Url = "https://example.test/file",
            FileName = "file.bin",
            Folder = Path.GetTempPath(),
            QueueId = 1,
            QueuePos = 1,
            TotalBytes = total,
            DoneBytes = done,
            CreatedUtc = _clock.GetUtcNow(),
        };
        await _jobs.AddAsync(job, TempDatabase.Ct);
        return job;
    }

    [Fact]
    public async Task Reservations_include_the_known_remaining_size_and_stop_the_next_job()
    {
        var queue = await QuotaQueueAsync(2_000_000_000);
        var first = await JobAsync(1_500_000_000);
        var second = await JobAsync(1_000_000_000);
        using var quota = new QueueQuotaService(_queues, _jobs, _usage, _clock);

        (await quota.TryReserveAsync(first, queue, TempDatabase.Ct)).Should().BeTrue();
        (await quota.TryReserveAsync(second, queue, TempDatabase.Ct)).Should().BeFalse();

        await quota.ReleaseAsync(first.Id, TempDatabase.Ct);
        (await quota.TryReserveAsync(second, queue, TempDatabase.Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Reported_bytes_reach_the_cap_and_raise_once()
    {
        var queue = await QuotaQueueAsync(1_000);
        var job = await JobAsync(1_000);
        using var quota = new QueueQuotaService(_queues, _jobs, _usage, _clock);
        (await quota.TryReserveAsync(job, queue, TempDatabase.Ct)).Should().BeTrue();
        var reached = 0;
        quota.QuotaReached += (_, queueId) =>
        {
            queueId.Should().Be(1);
            reached++;
        };

        quota.Report(job.Id, 600);
        quota.Report(job.Id, 400);
        await WaitForAsync(async () => await _usage.GetAsync(1, QuotaPeriod.Start(QuotaReset.Daily, _clock.GetLocalNow()), TempDatabase.Ct) == 1_000);

        reached.Should().Be(1);
        (await quota.HasCapacityAsync(1, TempDatabase.Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_local_midnight_has_a_new_period_and_capacity_again()
    {
        var queue = await QuotaQueueAsync(1_000);
        var job = await JobAsync(1_000);
        using var quota = new QueueQuotaService(_queues, _jobs, _usage, _clock);
        (await quota.TryReserveAsync(job, queue, TempDatabase.Ct)).Should().BeTrue();
        quota.Report(job.Id, 1_000);
        await WaitForAsync(async () => !await quota.HasCapacityAsync(1, TempDatabase.Ct));

        _clock.Advance(TimeSpan.FromHours(13));

        (await quota.HasCapacityAsync(1, TempDatabase.Ct)).Should().BeTrue();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("quota condition was not met");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
