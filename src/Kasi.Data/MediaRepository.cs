using Dapper;
using Kasi.Core;

namespace Kasi.Data;

/// <summary>SQLite persistence for media streams and segments; the M3.3 completion writer calls this in 1 s batches.</summary>
public sealed class MediaRepository(SqliteConnectionFactory factory) : IMediaRepository
{
    private const string StreamSelect =
        "SELECT idx AS \"Index\", role AS Role, language AS Language, codecs AS Codecs, container AS Container, " +
        "variant_id AS VariantId, estimated_bytes AS EstimatedBytes, init_url AS InitUrl, init_size AS InitSize FROM media_stream";

    private const string SegmentSelect =
        "SELECT stream_idx AS StreamIndex, idx AS \"Index\", url AS Url, size AS Size, completed AS Completed, " +
        "iv AS Iv, discontinuity AS Discontinuity, range_start AS RangeStart, range_end AS RangeEnd FROM media_segment";

    public async Task ReplaceAsync(
        JobId jobId,
        IReadOnlyList<MediaStreamRecord> streams,
        IReadOnlyList<MediaSegmentRecord> segments,
        long? estimatedTotalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(segments);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM media_segment WHERE job_id = @id; DELETE FROM media_stream WHERE job_id = @id;",
                new { id = jobId.ToString() },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var stream in streams)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO media_stream (job_id, idx, role, language, codecs, container, variant_id, estimated_bytes, init_url, init_size) " +
                "VALUES (@job, @index, @role, @language, @codecs, @container, @variantId, @estimatedBytes, @initUrl, @initSize);",
                new
                {
                    job = jobId.ToString(),
                    index = stream.Index,
                    role = stream.Role,
                    language = stream.Language,
                    codecs = stream.Codecs,
                    container = stream.Container,
                    variantId = stream.VariantId,
                    estimatedBytes = stream.EstimatedBytes,
                    initUrl = stream.InitUrl,
                    initSize = stream.InitSize,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (var segment in segments)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO media_segment (job_id, stream_idx, idx, url, size, completed, iv, discontinuity, range_start, range_end) " +
                "VALUES (@job, @stream, @index, @url, @size, @completed, @iv, @discontinuity, @rangeStart, @rangeEnd);",
                new
                {
                    job = jobId.ToString(),
                    stream = segment.StreamIndex,
                    index = segment.Index,
                    url = segment.Url,
                    size = segment.Size,
                    completed = segment.Completed ? 1 : 0,
                    iv = segment.Iv,
                    discontinuity = segment.Discontinuity ? 1 : 0,
                    rangeStart = segment.RangeStart,
                    rangeEnd = segment.RangeEnd,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET done_bytes = 0, total_bytes = COALESCE(@total, total_bytes) WHERE id = @id;",
            new { id = jobId.ToString(), total = estimatedTotalBytes },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaStreamRecord>> LoadStreamsAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MediaStreamRow>(
            new CommandDefinition($"{StreamSelect} WHERE job_id = @id ORDER BY idx;", new { id = jobId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => new MediaStreamRecord(
            row.Index,
            row.Role,
            row.Language,
            row.Codecs,
            row.Container,
            row.VariantId,
            row.EstimatedBytes,
            row.InitUrl,
            row.InitSize))];
    }

    public async Task<IReadOnlyList<MediaSegmentRecord>> LoadSegmentsAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MediaSegmentRow>(
            new CommandDefinition($"{SegmentSelect} WHERE job_id = @id ORDER BY stream_idx, idx;", new { id = jobId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => new MediaSegmentRecord(
            row.StreamIndex,
            row.Index,
            row.Url,
            row.Size,
            row.Completed != 0,
            row.Iv,
            row.Discontinuity != 0,
            row.RangeStart,
            row.RangeEnd))];
    }

    public async Task MarkCompletedAsync(
        JobId jobId,
        IReadOnlyList<MediaSegmentCompletion> completed,
        long? estimatedTotalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completed);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var segment in completed)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE media_segment SET completed = 1, size = @size WHERE job_id = @job AND stream_idx = @stream AND idx = @index;",
                new { job = jobId.ToString(), stream = segment.StreamIndex, index = segment.Index, size = segment.Size },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET done_bytes = COALESCE((SELECT SUM(size) FROM media_segment WHERE job_id = @id AND completed = 1), 0), " +
            "total_bytes = COALESCE(@total, total_bytes) WHERE id = @id;",
            new { id = jobId.ToString(), total = estimatedTotalBytes },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetCompletionAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE media_segment SET completed = 0, size = NULL WHERE job_id = @id; UPDATE job SET done_bytes = 0 WHERE id = @id;",
            new { id = jobId.ToString() },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class MediaStreamRow
    {
        public int Index { get; set; }

        public string Role { get; set; } = "";

        public string? Language { get; set; }

        public string? Codecs { get; set; }

        public string? Container { get; set; }

        public string? VariantId { get; set; }

        public long? EstimatedBytes { get; set; }

        public string? InitUrl { get; set; }

        public long? InitSize { get; set; }
    }

    private sealed class MediaSegmentRow
    {
        public int StreamIndex { get; set; }

        public int Index { get; set; }

        public string Url { get; set; } = "";

        public long? Size { get; set; }

        public long Completed { get; set; }

        public string? Iv { get; set; }

        public long Discontinuity { get; set; }

        public long? RangeStart { get; set; }

        public long? RangeEnd { get; set; }
    }
}
