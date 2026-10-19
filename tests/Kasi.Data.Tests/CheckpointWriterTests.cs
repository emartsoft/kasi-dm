using Kasi.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
public sealed class CheckpointWriterTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly JobId _a = JobId.New(TimeProvider.System);
    private readonly JobId _b = JobId.New(TimeProvider.System);
    private CheckpointWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        var jobs = new JobRepository(_db.Factory);
        var segments = new SegmentRepository(_db.Factory);
        foreach (var (id, pos) in new[] { (_a, 1.0), (_b, 2.0) })
        {
            await jobs.AddAsync(
                new Job { Id = id, Kind = JobKind.Http, Url = "u", FileName = "f", Folder = "d", QueueId = 1, QueuePos = pos, CreatedUtc = _time.GetUtcNow() },
                TempDatabase.Ct);
            await segments.ReplaceAsync(id, [new Segment(0, 0, 1000), new Segment(1, 1000, 2000)], TempDatabase.Ct);
        }

        _writer = new CheckpointWriter(_db.Factory, _time, _interval);
        _writer.Start();
    }

    public async ValueTask DisposeAsync() => await _writer.DisposeAsync();

    public void Dispose() => _db.Dispose();

    private async Task<long> WrittenAsync(JobId job, int segment) =>
        (long)(await _db.ScalarAsync($"SELECT written FROM segment WHERE job_id = '{job}' AND idx = {segment};"))!;

    private async Task<long> DoneBytesAsync(JobId job) => (long)(await _db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{job}';"))!;

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            (DateTime.UtcNow < deadline).Should().BeTrue("the condition should become true within 10 s");
            await Task.Delay(10, TempDatabase.Ct);
        }
    }

    [Fact]
    public async Task Updates_wait_for_the_interval_then_commit_together_with_the_job_byte_count()
    {
        _writer.Record(_a, 0, 400);
        _writer.Record(_a, 1, 100);

        _time.Advance(_interval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100, TempDatabase.Ct);
        (await WrittenAsync(_a, 0)).Should().Be(0, "nothing is committed before the interval has passed");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => _writer.CommitCount == 1);

        (await WrittenAsync(_a, 0)).Should().Be(400);
        (await WrittenAsync(_a, 1)).Should().Be(100);
        (await DoneBytesAsync(_a)).Should().Be(500);
    }

    [Fact]
    public async Task Many_updates_across_jobs_and_segments_become_one_transaction_per_cycle()
    {
        for (var i = 1; i <= 100; i++)
        {
            _writer.Record(_a, 0, i);
            _writer.Record(_a, 1, i);
            _writer.Record(_b, 0, i * 2);
        }

        _time.Advance(_interval);
        await EventuallyAsync(() => _writer.CommitCount == 1);
        await Task.Delay(100, TempDatabase.Ct);

        _writer.CommitCount.Should().Be(1);
        (await WrittenAsync(_a, 0)).Should().Be(100);
        (await WrittenAsync(_b, 0)).Should().Be(200);
    }

    [Fact]
    public async Task A_quiet_interval_commits_nothing()
    {
        _time.Advance(_interval * 5);
        await Task.Delay(100, TempDatabase.Ct);

        _writer.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Progress_never_moves_backwards_within_a_batch_or_across_batches()
    {
        _writer.Record(_a, 0, 500);
        _writer.Record(_a, 0, 300);
        await _writer.FlushAsync(TempDatabase.Ct);
        (await WrittenAsync(_a, 0)).Should().Be(500);

        _writer.Record(_a, 0, 400);
        await _writer.FlushAsync(TempDatabase.Ct);

        (await WrittenAsync(_a, 0)).Should().Be(500);
    }

    [Fact]
    public async Task A_value_past_the_end_of_the_range_is_clamped_so_the_segment_stays_loadable()
    {
        _writer.Record(_a, 0, 5000);
        await _writer.FlushAsync(TempDatabase.Ct);

        (await WrittenAsync(_a, 0)).Should().Be(1000);
        (await new SegmentRepository(_db.Factory).LoadAsync(_a, TempDatabase.Ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Flush_commits_at_once_without_waiting_for_the_interval()
    {
        _writer.Record(_b, 1, 750);

        await _writer.FlushAsync(TempDatabase.Ct);

        (await WrittenAsync(_b, 1)).Should().Be(750);
        (await DoneBytesAsync(_b)).Should().Be(750);
        _writer.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_commits_what_is_left_without_a_tick()
    {
        _writer.Record(_a, 0, 321);

        await _writer.DisposeAsync();

        (await WrittenAsync(_a, 0)).Should().Be(321);
    }

    [Fact]
    public async Task Recording_after_the_writer_is_stopped_is_an_error()
    {
        await _writer.DisposeAsync();

        var act = () => _writer.Record(_a, 0, 1);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Trait("TC", "TC-RES-011")]
    [Fact]
    public async Task The_before_commit_hook_runs_inside_the_open_transaction_and_a_failure_there_rolls_everything_back()
    {
        var inside = -1L;
        _writer.BeforeCommit = async () =>
        {
            // another connection must not see the uncommitted update yet
            inside = await WrittenAsync(_a, 0);
            throw new InvalidOperationException("killed mid-commit");
        };
        _writer.Record(_a, 0, 400);
        _writer.Record(_a, 1, 100);

        _time.Advance(_interval);
        await EventuallyAsync(() => _writer.FaultCount == 1);

        inside.Should().Be(0);
        (await WrittenAsync(_a, 0)).Should().Be(0, "the transaction was rolled back");
        (await DoneBytesAsync(_a)).Should().Be(0);

        _writer.BeforeCommit = null;
        _time.Advance(_interval);
        await EventuallyAsync(() => _writer.CommitCount == 1);
        (await DoneBytesAsync(_a)).Should().Be(500, "the kept updates commit later");
    }

    [Fact]
    public async Task A_failed_commit_keeps_the_updates_and_a_later_tick_commits_them()
    {
        await _db.ExecAsync("ALTER TABLE segment RENAME TO segment_hidden;");
        _writer.Record(_a, 0, 600);

        _time.Advance(_interval);
        await EventuallyAsync(() => _writer.FaultCount == 1);
        _writer.LastError.Should().BeOfType<SqliteException>();
        _writer.CommitCount.Should().Be(0);

        await _db.ExecAsync("ALTER TABLE segment_hidden RENAME TO segment;");
        _time.Advance(_interval);
        await EventuallyAsync(() => _writer.CommitCount == 1);

        (await WrittenAsync(_a, 0)).Should().Be(600);
        _writer.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Flush_reports_a_failed_commit_but_the_updates_survive_for_the_next_attempt()
    {
        await _db.ExecAsync("ALTER TABLE segment RENAME TO segment_hidden;");
        _writer.Record(_a, 0, 250);

        Func<Task> act = () => _writer.FlushAsync(TempDatabase.Ct);
        await act.Should().ThrowAsync<SqliteException>();

        await _db.ExecAsync("ALTER TABLE segment_hidden RENAME TO segment;");
        await _writer.FlushAsync(TempDatabase.Ct);
        (await WrittenAsync(_a, 0)).Should().Be(250);
    }

    [Fact]
    public async Task An_update_for_a_job_that_was_deleted_is_ignored_and_does_not_hold_up_the_others()
    {
        await new JobRepository(_db.Factory).DeleteAsync(_a, TempDatabase.Ct);
        _writer.Record(_a, 0, 100);
        _writer.Record(_b, 0, 200);

        await _writer.FlushAsync(TempDatabase.Ct);

        (await WrittenAsync(_b, 0)).Should().Be(200);
        _writer.FaultCount.Should().Be(0);
    }

    [Fact]
    public async Task Many_workers_recording_at_once_end_with_each_segments_highest_value()
    {
        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 1; i <= 1000; i++)
            {
                _writer.Record(w % 2 == 0 ? _a : _b, w % 4 < 2 ? 0 : 1, i);
            }
        }, TempDatabase.Ct));
        await Task.WhenAll(workers);

        await _writer.FlushAsync(TempDatabase.Ct);

        (await WrittenAsync(_a, 0)).Should().Be(1000);
        (await WrittenAsync(_a, 1)).Should().Be(1000);
        (await WrittenAsync(_b, 0)).Should().Be(1000);
        (await WrittenAsync(_b, 1)).Should().Be(1000);
    }

    [Theory]
    [InlineData(-1, 0L)]
    [InlineData(0, -1L)]
    public void Negative_values_are_rejected_at_the_call(int segment, long written)
    {
        var act = () => _writer.Record(_a, segment, written);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
