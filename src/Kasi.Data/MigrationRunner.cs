using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kasi.Data;

/// <summary>
/// Applies the numbered SQL scripts embedded under <c>Migrations/</c> (<c>NNNN_name.sql</c>) in one transaction
/// (TRD 11.4). Before upgrading an existing database it is copied to <c>&lt;db&gt;.bak</c>.
/// </summary>
public sealed class MigrationRunner
{
    private const string ResourcePrefix = "Migrations/";

    private readonly SqliteConnectionFactory _factory;
    private readonly IReadOnlyList<MigrationScript> _scripts;
    private readonly TimeProvider _time;

    public MigrationRunner(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
        : this(factory, LoadEmbeddedScripts(), timeProvider)
    {
    }

    internal MigrationRunner(SqliteConnectionFactory factory, IReadOnlyList<MigrationScript> scripts, TimeProvider? timeProvider = null)
    {
        _factory = factory;
        _time = timeProvider ?? TimeProvider.System;
        _scripts = [.. scripts.OrderBy(s => s.Version)];
        for (var i = 0; i < _scripts.Count; i++)
        {
            if (_scripts[i].Version != i + 1)
            {
                throw new ArgumentException($"Migration versions must run 1..N without gaps or duplicates; found {_scripts[i].Version} at position {i + 1}.", nameof(scripts));
            }
        }
    }

    public string BackupPath => _factory.DatabasePath + ".bak";

    /// <summary>Brings the database to the latest version and returns the number of migrations applied.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);", null, cancellationToken)
            .ConfigureAwait(false);

        var current = await CurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (current > _scripts.Count)
        {
            throw new InvalidOperationException($"The database is at schema version {current} but this build only knows {_scripts.Count}; refusing to open a newer database.");
        }

        var pending = _scripts.Skip(current).ToList();
        if (pending.Count == 0)
        {
            return 0;
        }

        if (current > 0)
        {
            await BackupAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var script in pending)
        {
            await ExecuteAsync(connection, script.Sql, transaction, cancellationToken).ConfigureAwait(false);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_version (version, applied_utc) VALUES ($v, $t);";
            record.Parameters.AddWithValue("$v", script.Version);
            record.Parameters.AddWithValue("$t", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pending.Count;
    }

    private async Task BackupAsync(SqliteConnection source, CancellationToken cancellationToken)
    {
        // BackupDatabase takes a consistent snapshot even while the WAL holds uncheckpointed pages; a plain file copy would not.
        File.Delete(BackupPath);
        var destinationString = new SqliteConnectionStringBuilder { DataSource = BackupPath, Pooling = false }.ToString();
        using var destination = new SqliteConnection(destinationString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task<int> CurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(version, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<MigrationScript> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal));
        foreach (var resource in resources)
        {
            var file = resource[ResourcePrefix.Length..];
            var separator = file.IndexOf('_', StringComparison.Ordinal);
            if (separator < 1 || !int.TryParse(file.AsSpan(0, separator), CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration '{file}' must be named NNNN_name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add(new MigrationScript(version, file, reader.ReadToEnd()));
        }

        return scripts;
    }
}
