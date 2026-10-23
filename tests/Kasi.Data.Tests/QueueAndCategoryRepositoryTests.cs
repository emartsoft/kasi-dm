using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-CAT-001")]
public sealed class QueueAndCategoryRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public async ValueTask InitializeAsync() => await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_seeded_main_queue_is_returned_with_the_model_defaults()
    {
        var queue = await new QueueRepository(_db.Factory).GetAsync(1, TempDatabase.Ct);

        queue.Should().BeEquivalentTo(new Queue { Id = 1, Name = "Main", SortOrder = 0 });
    }

    [Fact]
    public async Task Every_queue_column_survives_the_round_trip()
    {
        await _db.ExecAsync("INSERT INTO queue (id, name, max_parallel, connections, speed_limit_bps, quota_bytes, quota_reset, post_action, sort_order) " +
            "VALUES (7, 'Night', 2, 16, 500000, 2147483648, 'weekly', 'shutdown', 3);");

        var queue = await new QueueRepository(_db.Factory).GetAsync(7, TempDatabase.Ct);

        queue.Should().BeEquivalentTo(new Queue
        {
            Id = 7,
            Name = "Night",
            MaxParallel = 2,
            Connections = 16,
            SpeedLimitBps = 500_000,
            QuotaBytes = 2_147_483_648,
            QuotaReset = QuotaReset.Weekly,
            PostAction = "shutdown",
            SortOrder = 3,
        });
    }

    [Fact]
    public async Task Queues_are_listed_in_display_order_and_an_unknown_id_is_null()
    {
        await _db.ExecAsync("INSERT INTO queue (id, name, sort_order) VALUES (3, 'C', 1), (2, 'B', 1), (9, 'A', 0);");
        var repository = new QueueRepository(_db.Factory);

        (await repository.ListAsync(TempDatabase.Ct)).Select(q => q.Name).Should().Equal("Main", "A", "B", "C");
        (await repository.GetAsync(404, TempDatabase.Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_quota_reset_the_model_does_not_know_is_reported_not_guessed()
    {
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);
        await TempDatabase.ExecAsync(connection, "PRAGMA ignore_check_constraints = ON;");
        await TempDatabase.ExecAsync(connection, "INSERT INTO queue (id, name, sort_order, quota_reset) VALUES (8, 'Odd', 4, 'hourly');");

        Func<Task> act = () => new QueueRepository(_db.Factory).GetAsync(8, TempDatabase.Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Add_and_update_and_delete_user_queues_work_as_expected()
    {
        var repo = new QueueRepository(_db.Factory);
        var queue = new Queue
        {
            Id = 0,
            Name = "Downloads",
            MaxParallel = 3,
            Connections = 8,
            SpeedLimitBps = 100_000,
            QuotaBytes = 500_000_000,
            QuotaReset = QuotaReset.Daily,
            PostAction = "sleep",
            SortOrder = 2,
        };

        var id = await repo.AddAsync(queue, TempDatabase.Ct);
        id.Should().BeGreaterThan(1);

        var loaded = await repo.GetAsync(id, TempDatabase.Ct);
        loaded.Should().BeEquivalentTo(queue with { Id = id });

        var updated = queue with { Id = id, Name = "Renamed", MaxParallel = 1, QuotaReset = null };
        (await repo.UpdateAsync(updated, TempDatabase.Ct)).Should().BeTrue();

        var reloaded = await repo.GetAsync(id, TempDatabase.Ct);
        reloaded.Should().BeEquivalentTo(updated);

        (await repo.DeleteAsync(id, TempDatabase.Ct)).Should().BeTrue();
        (await repo.GetAsync(id, TempDatabase.Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Main_queue_cannot_be_deleted()
    {
        var repo = new QueueRepository(_db.Factory);
        Func<Task> act = () => repo.DeleteAsync(1, TempDatabase.Ct);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*main queue*");
    }

    [Fact]
    public async Task The_default_categories_are_listed_in_id_order()
    {
        var categories = await new CategoryRepository(_db.Factory).ListAsync(TempDatabase.Ct);

        categories.Select(c => c.Name).Should().Equal("General", "Compressed", "Documents", "Music", "Video", "Programs", "Images", "Torrents");
        categories.Should().OnlyContain(c => c.IsBuiltin);
    }

    [Fact]
    public async Task A_category_is_found_by_id_and_a_user_category_is_not_builtin()
    {
        await _db.ExecAsync("INSERT INTO category (id, name, folder, is_builtin) VALUES (20, 'Code', 'Downloads\\Code', 0);");
        var repository = new CategoryRepository(_db.Factory);

        (await repository.GetAsync(5, TempDatabase.Ct)).Should().BeEquivalentTo(new Category { Id = 5, Name = "Video", Folder = "Video", IsBuiltin = true });
        (await repository.GetAsync(20, TempDatabase.Ct)).Should().BeEquivalentTo(new Category { Id = 20, Name = "Code", Folder = "Downloads\\Code" });
        (await repository.GetAsync(404, TempDatabase.Ct)).Should().BeNull();
    }
}
