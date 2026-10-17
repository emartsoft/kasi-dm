using Kasi.Core;
using Microsoft.Data.Sqlite;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-002")]
public sealed class SegmentRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private JobRepository _jobs = null!;
    private SegmentRepository _segments = null!;
    private Job _job = null!;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        _jobs = new JobRepository(_db.Factory);
        _segments = new SegmentRepository(_db.Factory);
        _job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = "https://example.com/f.bin",
            FileName = "f.bin",
            Folder = "Video",
            QueueId = 1,
            QueuePos = 1.0,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _jobs.AddAsync(_job, TempDatabase.Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_job_without_segments_loads_an_empty_list()
    {
        (await _segments.LoadAsync(_job.Id, TempDatabase.Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Segments_round_trip_in_index_order_including_offsets_beyond_four_GiB()
    {
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        var segments = new[]
        {
            new Segment(2, fourGiB + 2_000_000, fourGiB + 3_000_000, 10),
            new Segment(0, 0, 1_000_000, 1_000_000),
            new Segment(1, 1_000_000, fourGiB + 2_000_000, 123_456),
        };

        await _segments.ReplaceAsync(_job.Id, segments, TempDatabase.Ct);

        (await _segments.LoadAsync(_job.Id, TempDatabase.Ct)).Should().BeEquivalentTo(segments.OrderBy(s => s.Index), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Replace_discards_the_previous_segments_and_an_empty_list_clears_them()
    {
        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 100), new Segment(1, 100, 200)], TempDatabase.Ct);

        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 50)], TempDatabase.Ct);
        (await _segments.LoadAsync(_job.Id, TempDatabase.Ct)).Should().ContainSingle().Which.End.Should().Be(50);

        await _segments.ReplaceAsync(_job.Id, [], TempDatabase.Ct);
        (await _segments.LoadAsync(_job.Id, TempDatabase.Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_replace_leaves_the_old_segments_and_byte_count_untouched()
    {
        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 100, 40), new Segment(1, 100, 200, 10)], TempDatabase.Ct);

        Func<Task> duplicateIndex = () => _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 10), new Segment(0, 10, 20)], TempDatabase.Ct);

        await duplicateIndex.Should().ThrowAsync<SqliteException>();
        (await _segments.LoadAsync(_job.Id, TempDatabase.Ct)).Select(s => (s.Index, s.Written)).Should().Equal((0, 40L), (1, 10L));
        (await _jobs.GetAsync(_job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(50);
    }

    [Fact]
    public async Task Replace_sets_the_job_byte_count_to_the_sum_written_so_a_restart_resets_it()
    {
        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 100, 60), new Segment(1, 100, 200, 25)], TempDatabase.Ct);
        (await _jobs.GetAsync(_job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(85);

        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 200)], TempDatabase.Ct);

        (await _jobs.GetAsync(_job.Id, TempDatabase.Ct))!.DoneBytes.Should().Be(0);
    }

    [Fact]
    public async Task Segments_of_a_job_that_does_not_exist_are_rejected()
    {
        Func<Task> act = () => _segments.ReplaceAsync(JobId.New(TimeProvider.System), [new Segment(0, 0, 10)], TempDatabase.Ct);

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Segments_of_other_jobs_are_not_touched()
    {
        var other = _job with { Id = JobId.New(TimeProvider.System), QueuePos = 2.0 };
        await _jobs.AddAsync(other, TempDatabase.Ct);
        await _segments.ReplaceAsync(other.Id, [new Segment(0, 0, 500, 5)], TempDatabase.Ct);

        await _segments.ReplaceAsync(_job.Id, [new Segment(0, 0, 100)], TempDatabase.Ct);

        (await _segments.LoadAsync(other.Id, TempDatabase.Ct)).Should().ContainSingle().Which.Written.Should().Be(5);
    }

    [Fact]
    public async Task A_stored_range_that_is_not_valid_is_reported_not_repaired()
    {
        await _db.ExecAsync($"INSERT INTO segment (job_id, idx, start, \"end\", written) VALUES ('{_job.Id}', 0, 100, 200, 101);");

        Func<Task> act = () => _segments.LoadAsync(_job.Id, TempDatabase.Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }
}
