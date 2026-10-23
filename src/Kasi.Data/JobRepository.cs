using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class JobRepository(SqliteConnectionFactory factory, TimeProvider? timeProvider = null) : IJobRepository
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const string Select =
        "SELECT id AS Id, kind AS Kind, url AS Url, final_url AS FinalUrl, referrer AS Referrer, file_name AS FileName, folder AS Folder, " +
        "total_bytes AS TotalBytes, done_bytes AS DoneBytes, state AS State, error AS Error, pause_reason AS PauseReason, restart_count AS RestartCount, etag AS ETag, last_modified AS LastModified, " +
        "resumable AS Resumable, connections AS Connections, speed_limit_bps AS SpeedLimitBps, checksum_algo AS ChecksumAlgo, checksum AS Checksum, " +
        "queue_id AS QueueId, queue_pos AS QueuePos, category_id AS CategoryId, created_utc AS CreatedUtc, completed_utc AS CompletedUtc FROM job";

    public async Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO job (id, kind, url, final_url, referrer, file_name, folder, total_bytes, done_bytes, state, error, pause_reason, restart_count, etag, last_modified, " +
            "resumable, connections, speed_limit_bps, checksum_algo, checksum, queue_id, queue_pos, category_id, created_utc, completed_utc) " +
            "VALUES (@Id, @Kind, @Url, @FinalUrl, @Referrer, @FileName, @Folder, @TotalBytes, @DoneBytes, @State, @Error, @PauseReason, @RestartCount, @ETag, @LastModified, " +
            "@Resumable, @Connections, @SpeedLimitBps, @ChecksumAlgo, @Checksum, @QueueId, @QueuePos, @CategoryId, @CreatedUtc, @CompletedUtc);",
            Parameters(job),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"{Select} WHERE id = @id;", new { id = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToJob();
    }

    public async Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"{Select} WHERE (@queueId IS NULL OR queue_id = @queueId) AND (@state IS NULL OR state = @state) ORDER BY queue_id, queue_pos, id;",
            new { queueId, state = state is { } s ? JobStateText.ToText(s) : null },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToJob())];
    }

    public async Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET url = @Url, final_url = @FinalUrl, referrer = @Referrer, file_name = @FileName, folder = @Folder, total_bytes = @TotalBytes, " +
            "done_bytes = MAX(done_bytes, @DoneBytes), state = @State, error = @Error, pause_reason = @PauseReason, restart_count = @RestartCount, etag = @ETag, last_modified = @LastModified, resumable = @Resumable, " +
            "connections = @Connections, speed_limit_bps = @SpeedLimitBps, checksum_algo = @ChecksumAlgo, checksum = @Checksum, queue_id = @QueueId, " +
            "queue_pos = @QueuePos, category_id = @CategoryId, completed_utc = @CompletedUtc WHERE id = @Id;",
            Parameters(job),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET final_url = @FinalUrl, file_name = @FileName, total_bytes = @TotalBytes, etag = @ETag, last_modified = @LastModified, " +
            "resumable = @Resumable WHERE id = @id;",
            new
            {
                id = id.ToString(),
                metadata.FinalUrl,
                metadata.FileName,
                metadata.TotalBytes,
                metadata.ETag,
                metadata.LastModified,
                Resumable = metadata.Resumable ? 1 : 0,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET url = @url, final_url = @finalUrl WHERE id = @id;",
            new { id = id.ToString(), url, finalUrl },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> UpdateQueuePositionAsync(JobId id, int queueId, double queuePos, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET queue_id = @queueId, queue_pos = @queuePos WHERE id = @id;",
            new { id = id.ToString(), queueId, queuePos },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET done_bytes = 0 WHERE id = @id;", new { id = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM job WHERE id = @id;", new { id = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> TryChangeStateAsync(
        JobId id,
        JobState fromState,
        JobState toState,
        string? failureMessage = null,
        PauseReason? pauseReason = null,
        CancellationToken cancellationToken = default)
    {
        if (toState == JobState.Failed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        }

        if (toState == JobState.Paused && pauseReason is null)
        {
            throw new ArgumentException("A paused job needs a pause reason.", nameof(pauseReason));
        }

        if (toState != JobState.Paused && pauseReason is not null)
        {
            throw new ArgumentException("Only a paused job may carry a pause reason.", nameof(pauseReason));
        }

        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET state = @to, error = @error, pause_reason = @pauseReason, " +
            "completed_utc = CASE WHEN @to = 'Completed' THEN @completedUtc ELSE completed_utc END " +
            "WHERE id = @id AND state = @from;",
            new
            {
                id = id.ToString(),
                from = JobStateText.ToText(fromState),
                to = JobStateText.ToText(toState),
                error = failureMessage,
                pauseReason = pauseReason is { } reason ? PauseReasonText.ToText(reason) : null,
                completedUtc = toState == JobState.Completed ? DbText.FromDate(_time.GetUtcNow()) : null,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> ReconcileProgressAsync(JobId id, long doneBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(doneBytes);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET done_bytes = @doneBytes WHERE id = @id;",
            new { id = id.ToString(), doneBytes },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> QuickCheckAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await connection.QuerySingleAsync<string>(
            new CommandDefinition("PRAGMA quick_check;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return string.Equals(result, "ok", StringComparison.Ordinal);
    }

    public async Task<int?> IncrementRestartCountAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                "UPDATE job SET restart_count = restart_count + 1 WHERE id = @id RETURNING restart_count;",
                new { id = id.ToString() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object Parameters(Job job) => new
    {
        Id = job.Id.ToString(),
        Kind = JobKindText.ToText(job.Kind),
        job.Url,
        job.FinalUrl,
        job.Referrer,
        job.FileName,
        job.Folder,
        job.TotalBytes,
        job.DoneBytes,
        State = JobStateText.ToText(job.State),
        job.Error,
        PauseReason = job.PauseReason is { } reason ? PauseReasonText.ToText(reason) : null,
        job.RestartCount,
        job.ETag,
        job.LastModified,
        Resumable = job.Resumable ? 1 : 0,
        job.Connections,
        job.SpeedLimitBps,
        job.ChecksumAlgo,
        job.Checksum,
        job.QueueId,
        job.QueuePos,
        job.CategoryId,
        CreatedUtc = DbText.FromDate(job.CreatedUtc),
        CompletedUtc = DbText.FromDate(job.CompletedUtc),
    };

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? FinalUrl { get; set; }

        public string? Referrer { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public long? TotalBytes { get; set; }

        public long DoneBytes { get; set; }

        public string State { get; set; } = string.Empty;

        public string? Error { get; set; }

        public string? PauseReason { get; set; }

        public long RestartCount { get; set; }

        public string? ETag { get; set; }

        public string? LastModified { get; set; }

        public long Resumable { get; set; }

        public long? Connections { get; set; }

        public long? SpeedLimitBps { get; set; }

        public string? ChecksumAlgo { get; set; }

        public string? Checksum { get; set; }

        public long QueueId { get; set; }

        public double QueuePos { get; set; }

        public long? CategoryId { get; set; }

        public string CreatedUtc { get; set; } = string.Empty;

        public string? CompletedUtc { get; set; }

        public Job ToJob() => new()
        {
            Id = JobId.TryParse(Id, out var id) ? id : throw new InvalidDataException($"Job id '{Id}' is not a version 7 UUID."),
            Kind = JobKindText.TryParse(Kind, out var kind) ? kind : throw new InvalidDataException($"Unknown job kind '{Kind}' in job {Id}."),
            Url = Url,
            FinalUrl = FinalUrl,
            Referrer = Referrer,
            FileName = FileName,
            Folder = Folder,
            TotalBytes = TotalBytes,
            DoneBytes = DoneBytes,
            State = JobStateText.TryParse(State, out var state) ? state : throw new InvalidDataException($"Unknown job state '{State}' in job {Id}."),
            Error = Error,
            PauseReason = PauseReason is null
                ? null
                : PauseReasonText.TryParse(PauseReason, out var pauseReason)
                    ? pauseReason
                    : throw new InvalidDataException($"Unknown pause reason '{PauseReason}' in job {Id}."),
            RestartCount = checked((int)RestartCount),
            ETag = ETag,
            LastModified = LastModified,
            Resumable = Resumable != 0,
            Connections = Connections is { } c ? checked((int)c) : null,
            SpeedLimitBps = SpeedLimitBps,
            ChecksumAlgo = ChecksumAlgo,
            Checksum = Checksum,
            QueueId = checked((int)QueueId),
            QueuePos = QueuePos,
            CategoryId = CategoryId is { } cat ? checked((int)cat) : null,
            CreatedUtc = DbText.ToDate(CreatedUtc),
            CompletedUtc = DbText.ToDateOrNull(CompletedUtc),
        };
    }
}
