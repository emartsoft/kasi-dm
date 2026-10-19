using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

/// <summary>Migration 0002: the settings table, the main queue and the default categories (TRD 10.1, 10.4, 11).</summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-CAT-001")]
public sealed class DefaultDataTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_new_database_has_the_main_queue_with_the_documented_defaults()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

        (await _db.ScalarAsync("SELECT name || '|' || max_parallel || '|' || IFNULL(connections, 'null') || '|' || IFNULL(speed_limit_bps, 'null') || '|' || IFNULL(quota_bytes, 'null') FROM queue WHERE id = 1;"))
            .Should().Be("Main|4|null|null|null");
        (await _db.ScalarAsync("SELECT COUNT(*) FROM queue;")).Should().Be(1L);
    }

    [Fact]
    public async Task A_new_database_has_the_default_categories_with_fixed_ids_and_relative_folders()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

        var rows = new List<string>();
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id || ':' || name || ':' || folder || ':' || is_builtin FROM category ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(TempDatabase.Ct);
        while (await reader.ReadAsync(TempDatabase.Ct))
        {
            rows.Add(reader.GetString(0));
        }

        rows.Should().Equal(
            "1:General::1",
            "2:Compressed:Compressed:1",
            "3:Documents:Documents:1",
            "4:Music:Music:1",
            "5:Video:Video:1",
            "6:Programs:Programs:1",
            "7:Images:Images:1",
            "8:Torrents:Torrents:1");
    }

    [Fact]
    public async Task The_settings_table_starts_empty_and_holds_text_by_key()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

        (await _db.ScalarAsync("SELECT COUNT(*) FROM setting;")).Should().Be(0L);
        await _db.ExecAsync("INSERT INTO setting (key, value) VALUES ('connections', '8');");
        Func<Task> duplicate = () => _db.ExecAsync("INSERT INTO setting (key, value) VALUES ('connections', '16');");
        await duplicate.Should().ThrowAsync<SqliteException>();
        Func<Task> nullValue = () => _db.ExecAsync("INSERT INTO setting (key, value) VALUES ('x', NULL);");
        await nullValue.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Upgrading_a_version_1_database_keeps_its_jobs_backs_it_up_and_adds_the_defaults()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        await using var stream = assembly.GetManifestResourceStream("Migrations/0001_initial.sql")!;
        using var reader = new StreamReader(stream);
        var first = new MigrationScript(1, "0001_initial.sql", await reader.ReadToEndAsync(TempDatabase.Ct));
        await new MigrationRunner(_db.Factory, [first]).MigrateAsync(TempDatabase.Ct);
        await _db.ExecAsync("INSERT INTO queue (id, name, sort_order) VALUES (5, 'Night', 9);");
        await _db.ExecAsync("INSERT INTO job (id, kind, url, file_name, folder, state, queue_id, queue_pos, created_utc) VALUES ('j', 'http', 'u', 'f', 'd', 'Queued', 5, 1.0, 't');");
        var runner = new MigrationRunner(_db.Factory);

        (await runner.MigrateAsync(TempDatabase.Ct)).Should().Be(2);

        File.Exists(runner.BackupPath).Should().BeTrue();
        (await _db.ScalarAsync("SELECT COUNT(*) FROM job;")).Should().Be(1L);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM queue;")).Should().Be(2L);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM category;")).Should().Be(8L);
    }
}
