using Kasi.Core;
using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-006")]
public sealed class JobRepositoryTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 10, 17, 12, 30, 15, 123, TimeSpan.Zero);

    private readonly TempDatabase _db = new();
    private JobRepository _jobs = null!;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        _jobs = new JobRepository(_db.Factory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    private static Job NewJob(double queuePos = 1.0, int queueId = 1, JobKind kind = JobKind.Http) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = kind,
        Url = "https://example.com/file.bin",
        FileName = "file.bin",
        Folder = "Video",
        QueueId = queueId,
        QueuePos = queuePos,
        CreatedUtc = _created,
    };

    private static Job FullJob() => NewJob(kind: JobKind.Media) with
    {
        FinalUrl = "https://cdn.example.com/a1b2/file.bin",
        Referrer = "https://example.com/download",
        TotalBytes = 21_474_836_480,
        DoneBytes = 5_368_709_120,
        State = JobState.Retrying,
        Error = "connection reset",
        ETag = "\"abc-123\"",
        LastModified = "Wed, 21 Oct 2026 07:28:00 GMT",
        Resumable = false,
        Connections = 16,
        SpeedLimitBps = 500_000,
        ChecksumAlgo = "sha256",
        Checksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        CategoryId = 5,
        CompletedUtc = _created.AddHours(2),
        QueuePos = 1.0000000000000002,
    };

    [Fact]
    public async Task A_job_with_every_field_set_comes_back_identical()
    {
        var job = FullJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);

        (await _jobs.GetAsync(job.Id, TempDatabase.Ct)).Should().BeEquivalentTo(job);
    }

    [Fact]
    public async Task A_minimal_job_comes_back_with_its_nulls_and_defaults()
    {
        var job = NewJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);

        var loaded = await _jobs.GetAsync(job.Id, TempDatabase.Ct);

        loaded.Should().BeEquivalentTo(job);
        loaded!.State.Should().Be(JobState.Queued);
        loaded.TotalBytes.Should().BeNull();
        loaded.CategoryId.Should().BeNull();
        loaded.Resumable.Should().BeTrue();
    }

    [Fact]
    public async Task A_date_with_an_offset_is_stored_as_the_same_instant_in_UTC()
    {
        var local = new DateTimeOffset(2026, 10, 17, 15, 30, 15, TimeSpan.FromHours(3));
        var job = NewJob() with { CreatedUtc = local };
        await _jobs.AddAsync(job, TempDatabase.Ct);

        var loaded = await _jobs.GetAsync(job.Id, TempDatabase.Ct);

        loaded!.CreatedUtc.Should().Be(local);
        loaded.CreatedUtc.Offset.Should().Be(TimeSpan.Zero);
        (await _db.ScalarAsync($"SELECT created_utc FROM job WHERE id = '{job.Id}';")).Should().Be("2026-10-17T12:30:15.0000000Z");
    }

    [Fact]
    public async Task An_unknown_id_is_null_and_a_duplicate_id_is_rejected()
    {
        var job = NewJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);

        (await _jobs.GetAsync(JobId.New(TimeProvider.System), TempDatabase.Ct)).Should().BeNull();
        Func<Task> duplicate = () => _jobs.AddAsync(job, TempDatabase.Ct);
        await duplicate.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task A_job_in_a_queue_or_category_that_does_not_exist_is_rejected()
    {
        Func<Task> badQueue = () => _jobs.AddAsync(NewJob(queueId: 99), TempDatabase.Ct);
        Func<Task> badCategory = () => _jobs.AddAsync(NewJob() with { CategoryId = 99 }, TempDatabase.Ct);

        await badQueue.Should().ThrowAsync<SqliteException>();
        await badCategory.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Jobs_are_listed_in_queue_order_and_can_be_filtered_by_queue_and_state()
    {
        await _db.ExecAsync("INSERT INTO queue (id, name, sort_order) VALUES (2, 'Night', 1);");
        var third = NewJob(queuePos: 3.0);
        var first = NewJob(queuePos: 1.0);
        var between = NewJob(queuePos: 1.5) with { State = JobState.Paused };
        var other = NewJob(queuePos: 0.5, queueId: 2);
        foreach (var job in new[] { third, first, between, other })
        {
            await _jobs.AddAsync(job, TempDatabase.Ct);
        }

        (await _jobs.ListAsync(cancellationToken: TempDatabase.Ct)).Select(j => j.Id).Should().Equal(first.Id, between.Id, third.Id, other.Id);
        (await _jobs.ListAsync(queueId: 2, cancellationToken: TempDatabase.Ct)).Select(j => j.Id).Should().Equal(other.Id);
        (await _jobs.ListAsync(state: JobState.Paused, cancellationToken: TempDatabase.Ct)).Select(j => j.Id).Should().Equal(between.Id);
        (await _jobs.ListAsync(1, JobState.Completed, TempDatabase.Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_writes_the_changeable_columns_but_not_id_kind_or_creation_time()
    {
        var job = NewJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);
        job.TryTransition(JobState.Probing, TimeProvider.System, out var probing).Should().BeTrue();
        var changed = probing with
        {
            Url = "https://example.com/refreshed.bin",
            FileName = "renamed.bin",
            TotalBytes = 1000,
            ETag = "\"v2\"",
            CategoryId = 3,
            Kind = JobKind.Torrent,
            CreatedUtc = _created.AddDays(1),
        };

        (await _jobs.UpdateAsync(changed, TempDatabase.Ct)).Should().BeTrue();

        (await _jobs.GetAsync(job.Id, TempDatabase.Ct)).Should().BeEquivalentTo(changed with { Kind = JobKind.Http, CreatedUtc = _created });
    }

    [Fact]
    public async Task Update_never_lowers_the_saved_byte_count_and_ResetProgress_does()
    {
        var job = NewJob() with { DoneBytes = 5000 };
        await _jobs.AddAsync(job, TempDatabase.Ct);

        await _jobs.UpdateAsync(job with { DoneBytes = 1000 }, TempDatabase.Ct);
        (await _jobs.GetAsync(job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(5000);

        await _jobs.UpdateAsync(job with { DoneBytes = 7000 }, TempDatabase.Ct);
        (await _jobs.GetAsync(job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(7000);

        (await _jobs.ResetProgressAsync(job.Id, TempDatabase.Ct)).Should().BeTrue();
        (await _jobs.GetAsync(job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(0);
    }

    [Fact]
    public async Task UpdateTransferMetadata_writes_only_the_probe_columns_and_never_a_stale_state()
    {
        var job = NewJob() with { State = JobState.Paused, Error = "User", DoneBytes = 5000, Connections = 3 };
        await _jobs.AddAsync(job, TempDatabase.Ct);
        var metadata = new JobTransferMetadata("https://cdn.example.com/f.bin", "f.bin", 12345, "\"abc\"", "Sun, 18 Oct 2026 10:00:00 GMT", Resumable: false);

        (await _jobs.UpdateTransferMetadataAsync(job.Id, metadata, TempDatabase.Ct)).Should().BeTrue();

        var stored = (await _jobs.GetAsync(job.Id, TempDatabase.Ct))!;
        stored.FinalUrl.Should().Be("https://cdn.example.com/f.bin");
        stored.FileName.Should().Be("f.bin");
        stored.TotalBytes.Should().Be(12345);
        stored.ETag.Should().Be("\"abc\"");
        stored.LastModified.Should().Be("Sun, 18 Oct 2026 10:00:00 GMT");
        stored.Resumable.Should().BeFalse();
        stored.State.Should().Be(JobState.Paused, "a probe result must not undo a state change made in the meantime");
        stored.Error.Should().Be("User");
        stored.DoneBytes.Should().Be(5000);
        stored.Connections.Should().Be(3);
        (await _jobs.UpdateTransferMetadataAsync(NewJob().Id, metadata, TempDatabase.Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Updating_resetting_or_deleting_a_missing_job_reports_false()
    {
        var ghost = NewJob();

        (await _jobs.UpdateAsync(ghost, TempDatabase.Ct)).Should().BeFalse();
        (await _jobs.ResetProgressAsync(ghost.Id, TempDatabase.Ct)).Should().BeFalse();
        (await _jobs.DeleteAsync(ghost.Id, TempDatabase.Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_job_removes_it_and_its_segments()
    {
        var job = NewJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);
        await _db.ExecAsync($"INSERT INTO segment (job_id, idx, start, \"end\", written) VALUES ('{job.Id}', 0, 0, 100, 0);");

        (await _jobs.DeleteAsync(job.Id, TempDatabase.Ct)).Should().BeTrue();

        (await _jobs.GetAsync(job.Id, TempDatabase.Ct)).Should().BeNull();
        (await _db.ScalarAsync("SELECT COUNT(*) FROM segment;")).Should().Be(0L);
    }

    [Theory]
    [InlineData("state", "Seeding")]
    [InlineData("kind", "gopher")]
    public async Task Stored_text_the_model_does_not_know_is_reported_not_guessed(string column, string value)
    {
        var job = NewJob();
        await _jobs.AddAsync(job, TempDatabase.Ct);
        await using var connection = await _db.Factory.OpenAsync(TempDatabase.Ct);
        await TempDatabase.ExecAsync(connection, "PRAGMA ignore_check_constraints = ON;");
        await TempDatabase.ExecAsync(connection, $"UPDATE job SET {column} = '{value}' WHERE id = '{job.Id}';");

        Func<Task> act = () => _jobs.GetAsync(job.Id, TempDatabase.Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_stored_id_that_is_not_a_version_7_uuid_is_reported()
    {
        await _db.ExecAsync("INSERT INTO job (id, kind, url, file_name, folder, state, queue_id, queue_pos, created_utc) VALUES ('not-an-id', 'http', 'u', 'f', 'd', 'Queued', 1, 1.0, '2026-10-17T12:30:15.0000000Z');");

        Func<Task> act = () => _jobs.ListAsync(cancellationToken: TempDatabase.Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }
}
