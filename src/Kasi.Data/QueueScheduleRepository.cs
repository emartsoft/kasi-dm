using System.Globalization;
using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class QueueScheduleRepository(SqliteConnectionFactory factory) : IQueueScheduleRepository
{
    private const string Select =
        "SELECT queue_id AS QueueId, is_enabled AS IsEnabled, start_time AS StartTime, stop_time AS StopTime, days_of_week AS DaysOfWeek, profile_id AS ProfileId FROM queue_schedule";

    public async Task<QueueSchedule?> GetAsync(int queueId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"{Select} WHERE queue_id = @queueId;", new { queueId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToSchedule();
    }

    public async Task<IReadOnlyList<QueueSchedule>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(
            new CommandDefinition($"{Select} ORDER BY queue_id;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToSchedule())];
    }

    public async Task UpsertAsync(QueueSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO queue_schedule (queue_id, is_enabled, start_time, stop_time, days_of_week, profile_id)
            VALUES (@queueId, @isEnabled, @startTime, @stopTime, @daysOfWeek, @profileId)
            ON CONFLICT(queue_id) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                start_time = excluded.start_time,
                stop_time = excluded.stop_time,
                days_of_week = excluded.days_of_week,
                profile_id = excluded.profile_id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                queueId = schedule.QueueId,
                isEnabled = schedule.IsEnabled ? 1 : 0,
                startTime = schedule.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                stopTime = schedule.StopTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                daysOfWeek = (int)schedule.DaysOfWeek,
                profileId = schedule.ProfileId,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int queueId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM queue_schedule WHERE queue_id = @queueId;",
            new { queueId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public long QueueId { get; set; }

        public long IsEnabled { get; set; }

        public string StartTime { get; set; } = string.Empty;

        public string? StopTime { get; set; }

        public long DaysOfWeek { get; set; }

        public long? ProfileId { get; set; }

        public QueueSchedule ToSchedule() => new()
        {
            QueueId = checked((int)QueueId),
            IsEnabled = IsEnabled != 0,
            StartTime = TimeOnly.Parse(StartTime, CultureInfo.InvariantCulture),
            StopTime = StopTime is null ? null : TimeOnly.Parse(StopTime, CultureInfo.InvariantCulture),
            DaysOfWeek = (DaysOfWeek)DaysOfWeek,
            ProfileId = ProfileId is { } profileId ? checked((int)profileId) : null,
        };
    }
}
