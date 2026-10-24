using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-QUE-003")]
public sealed class QueueScheduleRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public async ValueTask InitializeAsync() => await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Upsert_and_retrieve_and_delete_schedule_round_trips_cleanly()
    {
        var scheduleRepo = new QueueScheduleRepository(_db.Factory);

        // Queue 1 (Main) already exists from migration 0002
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0, 0),
            StopTime = new TimeOnly(6, 0, 0),
            DaysOfWeek = DaysOfWeek.Monday | DaysOfWeek.Wednesday | DaysOfWeek.Friday,
        };

        await scheduleRepo.UpsertAsync(schedule, TempDatabase.Ct);

        var loaded = await scheduleRepo.GetAsync(1, TempDatabase.Ct);
        loaded.Should().BeEquivalentTo(schedule);

        // Update existing schedule
        var updated = schedule with
        {
            IsEnabled = false,
            StopTime = null,
            DaysOfWeek = DaysOfWeek.All,
        };
        await scheduleRepo.UpsertAsync(updated, TempDatabase.Ct);

        var reloaded = await scheduleRepo.GetAsync(1, TempDatabase.Ct);
        reloaded.Should().BeEquivalentTo(updated);

        var list = await scheduleRepo.ListAsync(TempDatabase.Ct);
        list.Should().ContainSingle(s => s.QueueId == 1);

        (await scheduleRepo.DeleteAsync(1, TempDatabase.Ct)).Should().BeTrue();
        (await scheduleRepo.GetAsync(1, TempDatabase.Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_queue_cascades_and_deletes_its_schedule()
    {
        var queueRepo = new QueueRepository(_db.Factory);
        var scheduleRepo = new QueueScheduleRepository(_db.Factory);

        int qid = await queueRepo.AddAsync(new Queue { Id = 0, Name = "NightQueue", SortOrder = 5 }, TempDatabase.Ct);

        await scheduleRepo.UpsertAsync(new QueueSchedule
        {
            QueueId = qid,
            IsEnabled = true,
            StartTime = new TimeOnly(1, 0),
            StopTime = new TimeOnly(5, 0),
            DaysOfWeek = DaysOfWeek.All,
        }, TempDatabase.Ct);

        (await scheduleRepo.GetAsync(qid, TempDatabase.Ct)).Should().NotBeNull();

        // Delete user queue
        await queueRepo.DeleteAsync(qid, TempDatabase.Ct);

        // Schedule should be deleted by foreign key cascade
        (await scheduleRepo.GetAsync(qid, TempDatabase.Ct)).Should().BeNull();
    }
}
