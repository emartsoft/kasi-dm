using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-BWL-003")]
[Trait("TC", "TC-BWL-002")]
public sealed class SpeedProfileRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public async ValueTask InitializeAsync() => await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Profiles_round_trip_and_active_profile_is_persisted()
    {
        var repository = new SpeedProfileRepository(_db.Factory);
        var id = await repository.AddAsync(new SpeedProfile { Id = 0, Name = "Night", LimitBps = 2_000_000, SortOrder = 1 }, TempDatabase.Ct);

        var stored = await repository.GetAsync(id, TempDatabase.Ct);
        stored.Should().BeEquivalentTo(new SpeedProfile { Id = id, Name = "Night", LimitBps = 2_000_000, SortOrder = 1 });
        await repository.SetActiveProfileIdAsync(id, TempDatabase.Ct);
        (await new SpeedProfileRepository(_db.Factory).GetActiveProfileIdAsync(TempDatabase.Ct)).Should().Be(id);

        await repository.UpdateAsync(stored! with { LimitBps = null }, TempDatabase.Ct);
        (await repository.GetAsync(id, TempDatabase.Ct))!.LimitBps.Should().BeNull();
        await repository.SetActiveProfileIdAsync(null, TempDatabase.Ct);
        (await repository.GetActiveProfileIdAsync(TempDatabase.Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_profile_clears_a_schedule_reference()
    {
        var repository = new SpeedProfileRepository(_db.Factory);
        var id = await repository.AddAsync(new SpeedProfile { Id = 0, Name = "Day", LimitBps = 500_000, SortOrder = 0 }, TempDatabase.Ct);
        var schedules = new QueueScheduleRepository(_db.Factory);
        await schedules.UpsertAsync(new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(9, 0),
            DaysOfWeek = DaysOfWeek.All,
            ProfileId = id,
        }, TempDatabase.Ct);

        (await schedules.GetAsync(1, TempDatabase.Ct))!.ProfileId.Should().Be(id);
        await repository.DeleteAsync(id, TempDatabase.Ct);

        (await schedules.GetAsync(1, TempDatabase.Ct))!.ProfileId.Should().BeNull();
    }
}
