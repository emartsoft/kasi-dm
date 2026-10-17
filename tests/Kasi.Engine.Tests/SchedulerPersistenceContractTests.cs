using Kasi.Core;
using Kasi.Data;
using Kasi.Engine.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

/// <summary>
/// The contract of ADR 0004 section "Splits and the database", against the real <see cref="CheckpointWriter"/> and SQLite: a split
/// is persisted with <see cref="ISegmentRepository.ReplaceAsync"/> before progress of the new segment is recorded, because the
/// writer silently ignores segments that are not in the database.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
[Trait("Requirement", "KASI-ENG-005")]
public sealed class SchedulerPersistenceContractTests : IAsyncLifetime, IDisposable
{
    private const long Total = 48L * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-tests", Guid.NewGuid().ToString("N"));
    private readonly JobId _job = JobId.New(TimeProvider.System);
    private SqliteConnectionFactory _factory = null!;
    private SegmentRepository _segments = null!;
    private CheckpointWriter _writer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_folder, "kasi.db"));
        await new MigrationRunner(_factory).MigrateAsync(Ct);
        await new JobRepository(_factory).AddAsync(
            new Job
            {
                Id = _job,
                Kind = JobKind.Http,
                Url = "https://example.com/f.bin",
                FileName = "f.bin",
                Folder = "Video",
                QueueId = 1,
                QueuePos = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                TotalBytes = Total,
            },
            Ct);
        _segments = new SegmentRepository(_factory);
        _writer = new CheckpointWriter(_factory, interval: TimeSpan.FromHours(1)); // flushed explicitly below
        _writer.Start();
    }

    public async ValueTask DisposeAsync() => await _writer.DisposeAsync();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private async Task<long> DoneBytesAsync() => (await new JobRepository(_factory).GetAsync(_job, Ct))!.DoneBytes;

    /// <summary>Runs a download to the end, recording the received bytes as if each had been flushed; persists splits only if asked.</summary>
    private async Task RunAsync(bool persistSplits)
    {
        var sim = new SchedulerSimulator(Total, Enumerable.Repeat(4.0 * 1024 * 1024, 4).ToArray());
        await _segments.ReplaceAsync(_job, sim.Scheduler.GetSegmentsSnapshot(), Ct);
        var version = sim.Scheduler.StructureVersion;
        var steps = 0;

        while (!sim.Scheduler.IsComplete)
        {
            sim.Step();
            steps++;
            if (persistSplits && sim.Scheduler.StructureVersion != version)
            {
                await _segments.ReplaceAsync(_job, sim.Scheduler.GetSegmentsSnapshot(), Ct);
                version = sim.Scheduler.StructureVersion;
            }

            if (steps % 20 == 0)
            {
                RecordAll(sim);
                await _writer.FlushAsync(Ct);
            }
        }

        sim.Scheduler.StructureVersion.Should().BeGreaterThan(0, "the download must have split for this test to mean anything");
        if (persistSplits && sim.Scheduler.StructureVersion != version)
        {
            await _segments.ReplaceAsync(_job, sim.Scheduler.GetSegmentsSnapshot(), Ct);
        }

        RecordAll(sim);
        await _writer.FlushAsync(Ct);
        Final = sim.Scheduler.GetSegmentsSnapshot();
    }

    private IReadOnlyList<Segment> Final { get; set; } = [];

    private void RecordAll(SchedulerSimulator sim)
    {
        // the snapshot is taken before the (imagined) FlushFileBuffers and recorded after it: ADR 0004
        foreach (var segment in sim.Scheduler.GetSegmentsSnapshot())
        {
            _writer.Record(_job, segment.Index, segment.Written);
        }
    }

    [Fact]
    public async Task Persisting_each_split_before_recording_progress_leaves_the_database_equal_to_the_scheduler()
    {
        await RunAsync(persistSplits: true);

        var stored = await _segments.LoadAsync(_job, Ct);

        stored.Should().BeEquivalentTo(Final.OrderBy(s => s.Index), o => o.WithStrictOrdering()); // the repository loads by index, the scheduler lists by start
        Final.Should().HaveCountGreaterThan(1).And.OnlyContain(s => s.IsComplete);
        (await DoneBytesAsync()).Should().Be(Total);
    }

    [Fact]
    public async Task Without_persisting_the_splits_the_progress_of_the_new_segments_is_silently_lost()
    {
        await RunAsync(persistSplits: false);

        var stored = await _segments.LoadAsync(_job, Ct);

        stored.Should().ContainSingle("only the segment that existed at the start was ever stored");
        stored.Sum(s => s.Written).Should().BeLessThan(Total);
        (await DoneBytesAsync()).Should().BeLessThan(Total);
        Final.Should().HaveCountGreaterThan(1);
    }
}
