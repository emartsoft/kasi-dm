using Kasi.Core;
using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

/// <summary>The constraints of migration 0001 (TRD 11.2) do what the schema comments promise.</summary>
[Trait("Category", "Integration")]
public sealed class SchemaTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct); // queue 1 and the default categories come from migration 0002
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    private static string InsertJob(string id = "job-1", string kind = "http", int queueId = 1) =>
        $"INSERT INTO job (id, kind, url, file_name, folder, state, queue_id, queue_pos, created_utc) " +
        $"VALUES ('{id}', '{kind}', 'http://x/', 'f.bin', 'C:/d', 'Queued', {queueId}, 1.0, '2026-01-01T00:00:00Z');";

    [Fact]
    public async Task Creates_the_core_tables_and_job_indexes()
    {
        var names = new List<string>();
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
        await using var reader = await command.ExecuteReaderAsync(TempDatabase.Ct);
        while (await reader.ReadAsync(TempDatabase.Ct))
        {
            names.Add(reader.GetString(0));
        }

        names.Should().Contain(["queue", "category", "job", "segment", "job_secret", "schema_version", "queue_schedule", "ix_job_queue", "ix_job_created"]);
    }

    [Fact]
    public async Task Accepts_a_valid_job_segment_and_secret()
    {
        await _db.ExecAsync(InsertJob());
        await _db.ExecAsync("INSERT INTO segment (job_id, idx, start, \"end\", written) VALUES ('job-1', 0, 0, 100, 0);");
        await _db.ExecAsync("INSERT INTO job_secret (job_id, payload) VALUES ('job-1', x'00');");

        (await _db.ScalarAsync("SELECT COUNT(*) FROM segment;")).Should().Be(1L);
    }

    [Theory]
    [InlineData("INSERT INTO queue (id, name, sort_order, quota_reset) VALUES (2, 'Q2', 1, 'hourly');", "quota_reset outside daily/weekly/never")]
    [InlineData("INSERT INTO queue (id, name, sort_order) VALUES (2, 'Main', 1);", "duplicate queue name")]
    [InlineData("INSERT INTO category (name, folder) VALUES ('Video', 'v');", "duplicate category name")]
    public async Task Rejects_invalid_queue_and_category_rows(string sql, string reason)
    {
        Func<Task> act = () => _db.ExecAsync(sql);

        await act.Should().ThrowAsync<SqliteException>(reason);
    }

    [Theory]
    [InlineData(JobKind.Http)]
    [InlineData(JobKind.Ftp)]
    [InlineData(JobKind.Media)]
    [InlineData(JobKind.Torrent)]
    public async Task Accepts_every_job_kind_the_domain_model_can_write(JobKind kind)
    {
        await _db.ExecAsync(InsertJob(kind: JobKindText.ToText(kind)));

        (await _db.ScalarAsync("SELECT COUNT(*) FROM job;")).Should().Be(1L);
    }

    [Fact]
    public async Task Rejects_a_job_with_an_unknown_kind()
    {
        Func<Task> act = () => _db.ExecAsync(InsertJob(kind: "gopher"));

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Rejects_a_job_in_a_queue_that_does_not_exist()
    {
        Func<Task> act = () => _db.ExecAsync(InsertJob(queueId: 99));

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Deleting_a_job_cascades_to_its_segments_and_secret()
    {
        await _db.ExecAsync(InsertJob());
        await _db.ExecAsync("INSERT INTO segment (job_id, idx, start, \"end\", written) VALUES ('job-1', 0, 0, 100, 0);");
        await _db.ExecAsync("INSERT INTO job_secret (job_id, payload) VALUES ('job-1', x'00');");

        await _db.ExecAsync("DELETE FROM job WHERE id = 'job-1';");

        (await _db.ScalarAsync("SELECT (SELECT COUNT(*) FROM segment) + (SELECT COUNT(*) FROM job_secret);")).Should().Be(0L);
    }
}
