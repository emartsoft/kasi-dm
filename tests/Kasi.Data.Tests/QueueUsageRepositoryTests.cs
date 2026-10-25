using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-QUE-007")]
[Trait("TC", "TC-QUE-006")]
public sealed class QueueUsageRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public async ValueTask InitializeAsync() => await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Usage_is_accumulated_per_queue_and_period_and_survives_reopening()
    {
        var period = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var repository = new QueueUsageRepository(_db.Factory);

        (await repository.GetAsync(1, period, TempDatabase.Ct)).Should().Be(0);
        (await repository.AddAsync(1, period, 1_000, TempDatabase.Ct)).Should().Be(1_000);
        (await repository.AddAsync(1, period, 2_500, TempDatabase.Ct)).Should().Be(3_500);
        (await repository.AddAsync(1, period.AddDays(1), 400, TempDatabase.Ct)).Should().Be(400);

        (await repository.ListAsync(TempDatabase.Ct)).Should().HaveCount(2);
        (await new QueueUsageRepository(_db.Factory).GetAsync(1, period, TempDatabase.Ct)).Should().Be(3_500);
    }

    [Fact]
    public async Task Clearing_a_period_leaves_the_others_alone()
    {
        var period = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var repository = new QueueUsageRepository(_db.Factory);
        await repository.AddAsync(1, period, 10, TempDatabase.Ct);
        await repository.AddAsync(1, period.AddDays(7), 20, TempDatabase.Ct);

        await repository.ClearAsync(1, period, TempDatabase.Ct);

        (await repository.GetAsync(1, period, TempDatabase.Ct)).Should().Be(0);
        (await repository.GetAsync(1, period.AddDays(7), TempDatabase.Ct)).Should().Be(20);
    }

    [Fact]
    public async Task Negative_bytes_are_refused_by_the_repository_and_the_check_constraint()
    {
        var repository = new QueueUsageRepository(_db.Factory);
        Func<Task> act = () => repository.AddAsync(1, DateTimeOffset.UnixEpoch, -1, TempDatabase.Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
