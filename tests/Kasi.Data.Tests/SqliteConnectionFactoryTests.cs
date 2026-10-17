namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    [Trait("Requirement", "KASI-NFR-009")]
    public async Task Every_connection_uses_WAL_with_synchronous_normal_and_foreign_keys()
    {
        for (var i = 0; i < 2; i++)
        {
            await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);

            (await TempDatabase.ScalarAsync(connection, "PRAGMA journal_mode;")).Should().Be("wal");
            (await TempDatabase.ScalarAsync(connection, "PRAGMA synchronous;")).Should().Be(1L); // 1 = NORMAL
            (await TempDatabase.ScalarAsync(connection, "PRAGMA foreign_keys;")).Should().Be(1L);
        }
    }

    [Fact]
    public async Task Opening_creates_the_missing_database_folder()
    {
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);

        File.Exists(_db.Factory.DatabasePath).Should().BeTrue();
    }
}
