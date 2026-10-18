using System.Collections.Concurrent;
using Kasi.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

/// <summary>
/// The checkpoint order that makes resume safe (ADR 0003, ADR 0004, ADR 0008), proven with recording fakes instead of a live
/// download: the snapshot is taken before the flush, a split is persisted before progress is recorded, and nothing is
/// recorded when the flush fails.
/// </summary>
[Trait("Requirement", "KASI-RES-001")]
public sealed class SegmentCheckpointCoordinatorTests : IDisposable
{
    private const long FileSize = 8 * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _log = new();
    private readonly JobId _job = JobId.New(TimeProvider.System);
    private readonly PartFileWriter _writer;

    public SegmentCheckpointCoordinatorTests()
    {
        Directory.CreateDirectory(_folder);
        var opened = PartFileWriter.TryOpen(_folder, "coordinator.bin", FileSize).Should().BeOfType<PartFileOpened>().Subject;
        _writer = opened.Writer;
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_snapshot_is_taken_before_the_flush_so_bytes_counted_during_the_flush_are_not_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = NewScheduler(connections: 1);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(lease.LeaseId, 200_000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _writer.FlushFaultInjector = () =>
        {
            _log.Enqueue("flush");
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        };
        var coordinator = NewCoordinator(scheduler, _writer);

        var save = Task.Run(() => coordinator.SaveAsync(ct), ct);
        await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10)), ct);
        scheduler.AdvanceLease(lease.LeaseId, 100_000); // counted while FlushFileBuffers is running
        release.Set();
        await save;

        _log.Should().Contain("record:0:200000", "only the bytes counted before the flush started are known to be on disk");
        _log.Should().NotContain("record:0:300000");
    }

    [Fact]
    public async Task A_split_is_persisted_before_any_progress_is_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = NewScheduler(connections: 2);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        var coordinator = NewCoordinator(scheduler, _writer);
        await coordinator.SaveStructureAsync(ct); // the structure before the split
        _log.Clear();
        var second = scheduler.TryAcquireNextLease(1);
        second.Should().NotBeNull("a second worker splits the large segment");
        scheduler.AdvanceLease(first.LeaseId, 100_000);
        scheduler.AdvanceLease(second!.Value.LeaseId, 100_000);
        scheduler.StructureVersion.Should().BeGreaterThan(0);

        await coordinator.SaveAsync(ct);

        var entries = _log.ToArray();
        var replace = Array.FindIndex(entries, e => e.StartsWith("replace:", StringComparison.Ordinal));
        var firstRecord = Array.FindIndex(entries, e => e.StartsWith("record:", StringComparison.Ordinal));
        replace.Should().BeGreaterThanOrEqualTo(0);
        firstRecord.Should().BeGreaterThanOrEqualTo(0);
        replace.Should().BeLessThan(firstRecord, "the new segment's row must exist before its progress is recorded");
        Array.IndexOf(entries, "flush").Should().BeLessThan(replace, "the structure is persisted after the part file is flushed");
        entries.Should().EndWith("commit");
    }

    [Fact]
    public async Task A_failed_flush_persists_and_records_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = NewScheduler(connections: 1);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(lease.LeaseId, 100_000);
        _writer.FlushFaultInjector = () => new IOException("simulated flush failure");
        var coordinator = NewCoordinator(scheduler, _writer);

        var act = async () => await coordinator.SaveAsync(ct);

        await act.Should().ThrowAsync<SegmentCheckpointException>();
        _log.Should().BeEmpty("a cursor may be stored only after FlushFileBuffers has succeeded");
    }

    [Fact]
    public async Task A_database_hiccup_on_two_ticks_is_tolerated_and_a_good_tick_starts_the_count_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var scheduler = NewScheduler(connections: 1);
        var checkpoints = new RecordingCheckpoints(_log, failCommits: 2);
        var coordinator = NewCoordinator(scheduler, _writer, time, checkpoints, onFailure: _ => _log.Enqueue("stopped"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = coordinator.RunAsync(cts.Token);
        for (var tick = 1; tick <= 3; tick++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => checkpoints.Commits >= tick, ct);
        }

        coordinator.Failure.Should().BeNull();
        _log.Should().NotContain("stopped");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Three_failing_ticks_in_a_row_stop_the_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var scheduler = NewScheduler(connections: 1);
        var checkpoints = new RecordingCheckpoints(_log, failCommits: int.MaxValue);
        var coordinator = NewCoordinator(scheduler, _writer, time, checkpoints, onFailure: _ => _log.Enqueue("stopped"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = coordinator.RunAsync(cts.Token);
        for (var tick = 1; tick <= 3; tick++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => checkpoints.Commits >= tick, ct);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10), ct);
        coordinator.Failure.Should().BeOfType<InvalidOperationException>();
        _log.Count(e => e == "stopped").Should().Be(1);
    }

    [Fact]
    public async Task A_failed_part_file_flush_on_a_tick_stops_the_transfer_at_once_and_keeps_its_disk_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var scheduler = NewScheduler(connections: 1);
        _writer.FlushFaultInjector = () => new IOException("disk full", unchecked((int)0x80070070));
        var coordinator = NewCoordinator(scheduler, _writer, time, new RecordingCheckpoints(_log, failCommits: 0), onFailure: _ => _log.Enqueue("stopped"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = coordinator.RunAsync(cts.Token);
        time.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var failure = coordinator.Failure.Should().BeOfType<SegmentCheckpointException>().Subject;
        failure.Status.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        _log.Should().Contain("stopped");
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the tick should be processed");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static SegmentScheduler NewScheduler(int connections) => new(
        FileSize,
        rangesSupported: true,
        new SegmentSchedulerOptions { MaxConnections = connections, InitialConnections = connections });

    private SegmentCheckpointCoordinator NewCoordinator(
        SegmentScheduler scheduler,
        PartFileWriter writer,
        TimeProvider? time = null,
        RecordingCheckpoints? checkpoints = null,
        Action<Exception>? onFailure = null) => new(
        _job,
        checkpoints ?? new RecordingCheckpoints(_log, failCommits: 0),
        new RecordingSegments(_log),
        writer,
        scheduler,
        time ?? TimeProvider.System,
        TimeSpan.FromSeconds(1),
        onPersistentFailure: onFailure);

    private sealed class RecordingCheckpoints(ConcurrentQueue<string> log, int failCommits) : ICheckpointWriter
    {
        private int _commits;

        public int Commits => Volatile.Read(ref _commits);

        public void Record(JobId jobId, int segmentIndex, long written) => log.Enqueue($"record:{segmentIndex}:{written}");

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _commits);
            if (attempt <= failCommits)
            {
                return Task.FromException(new InvalidOperationException("simulated database failure"));
            }

            log.Enqueue("commit");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSegments(ConcurrentQueue<string> log) : ISegmentRepository
    {
        public Task<IReadOnlyList<Segment>> LoadAsync(JobId jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Segment>>([]);

        public Task ReplaceAsync(JobId jobId, IReadOnlyList<Segment> segments, CancellationToken cancellationToken = default)
        {
            log.Enqueue($"replace:{segments.Count}");
            return Task.CompletedTask;
        }
    }
}
