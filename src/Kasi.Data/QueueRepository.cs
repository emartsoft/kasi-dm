using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class QueueRepository(SqliteConnectionFactory factory) : IQueueRepository
{
    private const string Select =
        "SELECT id AS Id, name AS Name, max_parallel AS MaxParallel, connections AS Connections, speed_limit_bps AS SpeedLimitBps, " +
        "quota_bytes AS QuotaBytes, quota_reset AS QuotaReset, post_action AS PostAction, sort_order AS SortOrder FROM queue";

    public async Task<Queue?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"{Select} WHERE id = @id;", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToQueue();
    }

    public async Task<IReadOnlyList<Queue>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(
            new CommandDefinition($"{Select} ORDER BY sort_order, id;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToQueue())];
    }

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long MaxParallel { get; set; }

        public long? Connections { get; set; }

        public long? SpeedLimitBps { get; set; }

        public long? QuotaBytes { get; set; }

        public string? QuotaReset { get; set; }

        public string PostAction { get; set; } = string.Empty;

        public long SortOrder { get; set; }

        public Queue ToQueue() => new()
        {
            Id = checked((int)Id),
            Name = Name,
            MaxParallel = checked((int)MaxParallel),
            Connections = Connections is { } c ? checked((int)c) : null,
            SpeedLimitBps = SpeedLimitBps,
            QuotaBytes = QuotaBytes,
            QuotaReset = QuotaReset is null ? null : QuotaResetText.TryParse(QuotaReset, out var reset) ? reset : throw new InvalidDataException($"Unknown quota_reset '{QuotaReset}' in queue {Id}."),
            PostAction = PostAction,
            SortOrder = checked((int)SortOrder),
        };
    }
}
