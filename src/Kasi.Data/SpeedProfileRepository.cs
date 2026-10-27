using System.Globalization;
using Dapper;
using Kasi.Core;

namespace Kasi.Data;

/// <summary><see cref="ISpeedProfileRepository"/> over <c>speed_profile</c> and the active-profile setting (KASI-BWL-003).</summary>
public sealed class SpeedProfileRepository(SqliteConnectionFactory factory) : ISpeedProfileRepository
{
    private const string ActiveKey = "speed.active_profile_id";

    public async Task<IReadOnlyList<SpeedProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, limit_bps AS LimitBps, sort_order AS SortOrder FROM speed_profile ORDER BY sort_order, id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => row.ToProfile())];
    }

    public async Task<SpeedProfile?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, limit_bps AS LimitBps, sort_order AS SortOrder FROM speed_profile WHERE id = @id;",
            new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToProfile();
    }

    public async Task<int> AddAsync(SpeedProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO speed_profile (name, limit_bps, sort_order) VALUES (@name, @limitBps, @sortOrder); SELECT last_insert_rowid();",
            new { name = profile.Name, limitBps = profile.LimitBps, sortOrder = profile.SortOrder },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return checked((int)id);
    }

    public async Task<bool> UpdateAsync(SpeedProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE speed_profile SET name = @name, limit_bps = @limitBps, sort_order = @sortOrder WHERE id = @id;",
            new { id = profile.Id, name = profile.Name, limitBps = profile.LimitBps, sortOrder = profile.SortOrder },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM speed_profile WHERE id = @id;",
            new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var value = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM setting WHERE key = @key;",
            new { key = ActiveKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    public async Task SetActiveProfileIdAsync(int? profileId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (profileId is { } id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO setting (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = ActiveKey, value = id.ToString(CultureInfo.InvariantCulture) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM setting WHERE key = @key;",
                new { key = ActiveKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long? LimitBps { get; set; }

        public long SortOrder { get; set; }

        public SpeedProfile ToProfile() => new()
        {
            Id = checked((int)Id),
            Name = Name,
            LimitBps = LimitBps,
            SortOrder = checked((int)SortOrder),
        };
    }
#pragma warning restore CA1812
}
