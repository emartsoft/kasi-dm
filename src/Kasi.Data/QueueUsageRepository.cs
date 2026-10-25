using Dapper;
using Kasi.Core;

namespace Kasi.Data;

/// <summary><see cref="IQueueUsageRepository"/> over the <c>queue_usage</c> table (KASI-QUE-007).</summary>
public sealed class QueueUsageRepository(SqliteConnectionFactory factory) : IQueueUsageRepository
{
    public async Task<long> GetAsync(int queueId, DateTimeOffset periodStart, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT bytes FROM queue_usage WHERE queue_id = @queueId AND period_start = @periodStart;",
            new { queueId, periodStart = Text(periodStart) },
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;
    }

    public async Task<long> AddAsync(int queueId, DateTimeOffset periodStart, long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO queue_usage (queue_id, period_start, bytes) VALUES (@queueId, @periodStart, @bytes)
            ON CONFLICT (queue_id, period_start) DO UPDATE SET bytes = bytes + excluded.bytes
            RETURNING bytes;
            """,
            new { queueId, periodStart = Text(periodStart), bytes },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueueUsage>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT queue_id AS QueueId, period_start AS PeriodStart, bytes AS Bytes FROM queue_usage ORDER BY queue_id, period_start;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => new QueueUsage
        {
            QueueId = row.QueueId,
            PeriodStart = DateTimeOffset.Parse(row.PeriodStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            Bytes = row.Bytes,
        })];
    }

    public async Task ClearAsync(int queueId, DateTimeOffset periodStart, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM queue_usage WHERE queue_id = @queueId AND period_start = @periodStart;",
            new { queueId, periodStart = Text(periodStart) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string Text(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
    {
        public int QueueId { get; set; }

        public string PeriodStart { get; set; } = string.Empty;

        public long Bytes { get; set; }
    }
#pragma warning restore CA1812
}
