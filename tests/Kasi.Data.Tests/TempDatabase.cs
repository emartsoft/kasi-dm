using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

/// <summary>A database file in a throw-away folder; pooled connections are released before the folder is deleted.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-tests", Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Factory = new SqliteConnectionFactory(Path.Combine(_folder, "nested", "kasi.db"));
    }

    public SqliteConnectionFactory Factory { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<int> ExecAsync(string sql)
    {
        await using var connection = await Factory.OpenAsync(Ct);
        return await ExecAsync(connection, sql);
    }

    public static async Task<int> ExecAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(Ct);
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await Factory.OpenAsync(Ct);
        return await ScalarAsync(connection, sql);
    }

    public static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
