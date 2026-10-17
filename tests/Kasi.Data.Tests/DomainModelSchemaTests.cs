using System.Reflection;
using Kasi.Core;

namespace Kasi.Data.Tests;

/// <summary>Guards the domain model against drifting away from the TRD 11.2 tables it is stored in.</summary>
[Trait("Category", "Integration")]
public sealed class DomainModelSchemaTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();

    public static TheoryData<string, Type> Entities => new()
    {
        { "job", typeof(Job) },
        { "queue", typeof(Queue) },
        { "category", typeof(Category) },
    };

    public async ValueTask InitializeAsync() => await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Entity_properties_match_the_table_columns_one_to_one(string table, Type entity)
    {
        var columns = new List<string>();
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync(TempDatabase.Ct);
        while (await reader.ReadAsync(TempDatabase.Ct))
        {
            columns.Add(Normalise(reader.GetString(0)));
        }

        var properties = entity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => Normalise(p.Name));

        properties.Should().BeEquivalentTo(columns);
    }

    [Fact]
    public async Task Queue_defaults_match_the_column_defaults()
    {
        await _db.ExecAsync("INSERT INTO queue (id, name, sort_order) VALUES (1, 'Main', 0);");
        var model = new Queue { Id = 1, Name = "Main", SortOrder = 0 };

        (await _db.ScalarAsync("SELECT max_parallel FROM queue WHERE id = 1;")).Should().Be((long)model.MaxParallel);
        (await _db.ScalarAsync("SELECT post_action FROM queue WHERE id = 1;")).Should().Be(model.PostAction);
    }

    [Fact]
    public async Task Job_resumable_default_matches_the_column_default()
    {
        await _db.ExecAsync("INSERT INTO queue (id, name, sort_order) VALUES (1, 'Main', 0);");
        await _db.ExecAsync("INSERT INTO job (id, kind, url, file_name, folder, state, queue_id, queue_pos, created_utc) VALUES ('j', 'http', 'u', 'f', 'd', 'Queued', 1, 1.0, 't');");

        (await _db.ScalarAsync("SELECT resumable FROM job;")).Should().Be(1L);
        (await _db.ScalarAsync("SELECT done_bytes FROM job;")).Should().Be(0L);
    }

    [Theory]
    [InlineData(QuotaReset.Daily)]
    [InlineData(QuotaReset.Weekly)]
    [InlineData(QuotaReset.Never)]
    public async Task Every_quota_reset_the_domain_model_can_write_passes_the_CHECK_constraint(QuotaReset reset)
    {
        await _db.ExecAsync($"INSERT INTO queue (id, name, sort_order, quota_reset) VALUES (1, 'Q', 0, '{QuotaResetText.ToText(reset)}');");

        (await _db.ScalarAsync("SELECT COUNT(*) FROM queue;")).Should().Be(1L);
    }

    private static string Normalise(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
