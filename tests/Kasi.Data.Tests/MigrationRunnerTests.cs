using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
public sealed class MigrationRunnerTests : IDisposable
{
    private static readonly MigrationScript _first = new(1, "0001_first.sql", "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);");
    private static readonly MigrationScript _second = new(2, "0002_second.sql", "ALTER TABLE t ADD COLUMN y INTEGER;");
    private static readonly MigrationScript _broken = new(2, "0002_broken.sql", "CREATE TABLE half_done (x INTEGER); THIS IS NOT SQL;");

    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private MigrationRunner Runner(params MigrationScript[] scripts) => new(_db.Factory, scripts);

    [Fact]
    [Trait("Requirement", "KASI-RES-006")]
    public async Task Fresh_database_is_migrated_to_the_latest_version_and_rerun_is_a_no_op()
    {
        var runner = new MigrationRunner(_db.Factory);

        (await runner.MigrateAsync(TempDatabase.Ct)).Should().Be(10);
        (await runner.MigrateAsync(TempDatabase.Ct)).Should().Be(0);

        (await _db.ScalarAsync("SELECT MAX(version) FROM schema_version;")).Should().Be(10L);
        File.Exists(runner.BackupPath).Should().BeFalse("a fresh database has nothing worth backing up");
    }

    [Fact]
    [Trait("Requirement", "KASI-RES-006")]
    public async Task Upgrading_an_existing_database_leaves_a_backup_of_the_previous_version()
    {
        await Runner(_first).MigrateAsync(TempDatabase.Ct);
        var runner = Runner(_first, _second);

        (await runner.MigrateAsync(TempDatabase.Ct)).Should().Be(1);

        File.Exists(runner.BackupPath).Should().BeTrue();
        var backup = new SqliteConnectionStringBuilder { DataSource = runner.BackupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(backup);
        await connection.OpenAsync(TempDatabase.Ct);
        (await TempDatabase.ScalarAsync(connection, "SELECT MAX(version) FROM schema_version;")).Should().Be(1L);
        (await TempDatabase.ScalarAsync(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
        (await TempDatabase.ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('t') WHERE name = 'y';")).Should().Be(0L);
    }

    [Fact]
    [Trait("Requirement", "KASI-RES-006")]
    public async Task Failed_upgrade_rolls_back_completely_and_keeps_existing_data()
    {
        await Runner(_first).MigrateAsync(TempDatabase.Ct);

        Func<Task> act = () => Runner(_first, _broken).MigrateAsync(TempDatabase.Ct);

        await act.Should().ThrowAsync<SqliteException>();
        (await _db.ScalarAsync("SELECT MAX(version) FROM schema_version;")).Should().Be(1L);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done';")).Should().Be(0L);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM t;")).Should().Be(1L);
    }

    [Fact]
    [Trait("Requirement", "KASI-RES-006")]
    public async Task Failure_on_a_fresh_database_applies_nothing()
    {
        Func<Task> act = () => Runner(_first, _broken).MigrateAsync(TempDatabase.Ct);

        await act.Should().ThrowAsync<SqliteException>();
        (await _db.ScalarAsync("SELECT COUNT(*) FROM schema_version;")).Should().Be(0L);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('t', 'half_done');")).Should().Be(0L);
    }

    [Fact]
    [Trait("Requirement", "KASI-RES-006")]
    public async Task Database_from_a_newer_build_is_refused_and_left_untouched()
    {
        await Runner(_first, _second).MigrateAsync(TempDatabase.Ct);

        Func<Task> act = () => Runner(_first).MigrateAsync(TempDatabase.Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newer*");
        (await _db.ScalarAsync("SELECT MAX(version) FROM schema_version;")).Should().Be(2L);
    }

    [Fact]
    public void Gaps_in_the_migration_numbering_are_rejected()
    {
        var act = () => Runner(_first, new MigrationScript(3, "0003_third.sql", "SELECT 1;"));

        act.Should().Throw<ArgumentException>();
    }
}
