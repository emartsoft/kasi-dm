using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class SegmentRepository(SqliteConnectionFactory factory) : ISegmentRepository
{
    public async Task<IReadOnlyList<Segment>> LoadAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT idx AS Idx, start AS Start, \"end\" AS \"End\", written AS Written FROM segment WHERE job_id = @jobId ORDER BY idx;",
            new { jobId = jobId.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToSegment(jobId))];
    }

    public async Task ReplaceAsync(JobId jobId, IReadOnlyList<Segment> segments, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = jobId.ToString();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM segment WHERE job_id = @id;", new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var segment in segments)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO segment (job_id, idx, start, \"end\", written) VALUES (@id, @Index, @Start, @End, @Written);",
                new { id, segment.Index, segment.Start, segment.End, segment.Written },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET done_bytes = (SELECT COALESCE(SUM(written), 0) FROM segment WHERE job_id = @id) WHERE id = @id;",
            new { id },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public long Idx { get; set; }

        public long Start { get; set; }

        public long End { get; set; }

        public long Written { get; set; }

        public Segment ToSegment(JobId jobId)
        {
            try
            {
                return new Segment(checked((int)Idx), Start, End, Written);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException($"Segment {Idx} of job {jobId} is not a valid range ({Start}, {End}, written {Written}).", ex);
            }
        }
    }
}
