using Kasi.Data;
using Microsoft.Data.Sqlite;

namespace Kasi.Scheduling.Tests;

internal sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-scheduling-tests", Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Factory = new SqliteConnectionFactory(Path.Combine(_folder, "kasi.db"));
    }

    public SqliteConnectionFactory Factory { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task InitializeAsync()
    {
        await new MigrationRunner(Factory).MigrateAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // temporary test folder cleanup is best-effort
            }
        }
    }
}
