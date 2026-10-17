using Kasi.Core;
using Kasi.Engine.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-005")]
[Trait("Requirement", "KASI-ENG-006")]
public class SegmentSchedulerTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    private static SegmentScheduler Make(
        long total,
        int workers = 8,
        bool rangesSupported = true,
        bool probe = false,
        FakeTimeProvider? time = null) =>
        new(total, rangesSupported, new SegmentSchedulerOptions { MaxConnections = workers }, time ?? new FakeTimeProvider(), probe);

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void ReadFully(SegmentScheduler scheduler, LeaseAssignment lease)
    {
        scheduler.AdvanceLease(lease.LeaseId, checked((int)lease.Length)).IsLeaseComplete.Should().BeTrue();
        scheduler.CompleteLease(lease.LeaseId);
    }

    // ---- fresh start ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_file_is_complete_at_once_and_has_no_segments()
    {
        var scheduler = Make(0);

        scheduler.IsComplete.Should().BeTrue();
        scheduler.GetSegmentsSnapshot().Should().BeEmpty();
        scheduler.TryAcquireNextLease(0).Should().BeNull();
    }

    [Fact]
    public void A_new_download_is_one_segment_over_the_whole_file()
    {
        var scheduler = Make(10 * MiB);

        scheduler.GetSegmentsSnapshot().Should().ContainSingle().Which.Should().BeEquivalentTo(new Segment(0, 0, 10 * MiB));
        scheduler.IsComplete.Should().BeFalse();
        scheduler.TotalBytes.Should().Be(10 * MiB);
    }

    [Fact]
    public void A_cold_worker_gets_the_minimum_lease_from_the_start_of_the_file()
    {
        var lease = Make(100 * MiB).TryAcquireNextLease(0)!.Value;

        (lease.WorkerId, lease.SegmentIndex, lease.Start, lease.End).Should().Be((0, 0, 0L, 512 * KiB));
        lease.IsEndgameDuplicate.Should().BeFalse();
        lease.IsInitialProbeStream.Should().BeFalse();
    }

    [Fact]
    public void A_lease_never_extends_past_the_end_of_its_segment()
    {
        Make(300 * KiB).TryAcquireNextLease(0)!.Value.End.Should().Be(300 * KiB);
    }

    [Fact]
    public void A_tiny_trailing_remainder_is_folded_into_the_lease_instead_of_a_request_of_its_own()
    {
        var scheduler = Make((512 + 88) * KiB);

        scheduler.TryAcquireNextLease(0)!.Value.End.Should().Be((512 + 88) * KiB);
    }

    [Fact]
    public void The_first_lease_is_flagged_as_the_probe_stream_and_is_exactly_the_probed_range()
    {
        var scheduler = Make(100 * MiB, probe: true);

        var first = scheduler.TryAcquireNextLease(0)!.Value;
        var second = scheduler.TryAcquireNextLease(1)!.Value;

        first.IsInitialProbeStream.Should().BeTrue();
        (first.Start, first.End).Should().Be((0L, 512 * KiB));
        second.IsInitialProbeStream.Should().BeFalse();
    }

    [Fact]
    public void The_probe_lease_is_not_stretched_to_absorb_a_small_remainder()
    {
        var first = Make((512 + 88) * KiB, probe: true).TryAcquireNextLease(0)!.Value;

        (first.Start, first.End).Should().Be((0L, 512 * KiB));
    }

    [Fact]
    public void The_probe_flag_goes_to_worker_0_only_and_only_once()
    {
        var scheduler = Make(100 * MiB, probe: true);

        scheduler.TryAcquireNextLease(1)!.Value.IsInitialProbeStream.Should().BeFalse();
        scheduler.TryAcquireNextLease(0)!.Value.IsInitialProbeStream.Should().BeFalse("the flag marks only the very first lease");
    }

    // ---- splitting (TRD 6.3 TrySplit, TC-ENG-011) ----------------------------------------------------------------

    [Fact]
    public void An_idle_worker_splits_the_segment_in_two_after_the_lease_in_flight()
    {
        const long total = 100 * MiB;
        var scheduler = Make(total);
        var first = scheduler.TryAcquireNextLease(0)!.Value;

        var second = scheduler.TryAcquireNextLease(1)!.Value;

        var free = total - first.End;
        var mid = AlignUp(first.End + (free / 2), 64 * KiB);
        scheduler.StructureVersion.Should().Be(1);
        scheduler.GetSegmentsSnapshot().Should().BeEquivalentTo(
            [new Segment(0, 0, mid), new Segment(1, mid, total)],
            o => o.WithStrictOrdering());
        (second.WorkerId, second.SegmentIndex, second.Start, second.End).Should().Be((1, 1, mid, mid + (512 * KiB)));
        mid.Should().BeGreaterThanOrEqualTo(first.End, "a split never cuts into the lease in flight");
        (mid % (64 * KiB)).Should().Be(0);
        scheduler.AssertInvariants();
    }

    [Fact]
    public void The_lease_in_flight_when_the_split_happens_completes_normally_and_its_worker_continues_its_shortened_segment()
    {
        var scheduler = Make(100 * MiB);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        _ = scheduler.TryAcquireNextLease(1);

        var result = scheduler.AdvanceLease(first.LeaseId, checked((int)first.Length));
        scheduler.CompleteLease(first.LeaseId);
        var next = scheduler.TryAcquireNextLease(0)!.Value;

        result.IsLeaseComplete.Should().BeTrue();
        result.IsSuperseded.Should().BeFalse();
        (result.AcceptedStart, result.AcceptedLength).Should().Be((0L, first.Length));
        next.SegmentIndex.Should().Be(0);
        next.Start.Should().Be(first.End);
    }

    [Theory]
    [InlineData(2_621_440L, true)]   // 512 KiB leased, exactly 2 MiB free: the smallest split
    [InlineData(2_621_439L, false)]  // one byte less: too small to be worth a connection
    public void A_segment_is_split_only_when_at_least_two_MinSplit_remain_unleased(long total, bool splits)
    {
        var scheduler = Make(total);
        _ = scheduler.TryAcquireNextLease(0);

        var second = scheduler.TryAcquireNextLease(1);

        (second is not null).Should().Be(splits);
        scheduler.StructureVersion.Should().Be(splits ? 1 : 0);
    }

    [Fact]
    public void The_smallest_split_leaves_the_new_segment_one_MiB_aligned_to_64_KiB()
    {
        var scheduler = Make(2_621_440);
        _ = scheduler.TryAcquireNextLease(0);

        _ = scheduler.TryAcquireNextLease(1);

        scheduler.GetSegmentsSnapshot().Should().BeEquivalentTo(
            [new Segment(0, 0, 1_572_864), new Segment(1, 1_572_864, 2_621_440)],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void When_alignment_would_leave_nothing_the_split_falls_back_to_an_unaligned_point_inside_the_segment()
    {
        const long total = 100 * MiB;
        var scheduler = new SegmentScheduler(
            total,
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 2, PageAlignment = 1 * GiB },
            new FakeTimeProvider());
        var first = scheduler.TryAcquireNextLease(0)!.Value;

        var second = scheduler.TryAcquireNextLease(1)!.Value;

        var mid = first.End + ((total - first.End) / 2);
        second.Start.Should().Be(mid);
        scheduler.GetSegmentsSnapshot()[0].End.Should().Be(mid).And.BeGreaterThanOrEqualTo(first.End);
    }

    [Fact]
    public void The_segment_with_the_most_unleased_bytes_is_the_one_that_gets_split()
    {
        var scheduler = Make(200 * MiB);
        _ = scheduler.TryAcquireNextLease(0);
        _ = scheduler.TryAcquireNextLease(1);   // splits segment 0 in two halves of about 100 MiB
        var before = scheduler.GetSegmentsSnapshot();
        var biggest = before.OrderByDescending(s => s.Length).First();

        _ = scheduler.TryAcquireNextLease(2);

        var after = scheduler.GetSegmentsSnapshot();
        after.Should().HaveCount(3);
        after.Single(s => s.Index == biggest.Index).End.Should().BeLessThan(biggest.End);
        scheduler.StructureVersion.Should().Be(2);
    }

    [Fact]
    public void Snapshots_are_ordered_by_start_with_unique_indexes_after_several_splits()
    {
        var scheduler = Make(200 * MiB);
        for (var worker = 0; worker < 6; worker++)
        {
            _ = scheduler.TryAcquireNextLease(worker);
        }

        var snapshot = scheduler.GetSegmentsSnapshot();

        snapshot.Select(s => s.Start).Should().BeInAscendingOrder();
        snapshot.Select(s => s.Index).Should().OnlyHaveUniqueItems().And.HaveCount(6);
        snapshot[0].Start.Should().Be(0);
        snapshot[^1].End.Should().Be(200 * MiB);
        for (var i = 1; i < snapshot.Count; i++)
        {
            snapshot[i].Start.Should().Be(snapshot[i - 1].End);
        }
    }

    [Fact]
    public void A_snapshot_is_a_copy_and_does_not_change_when_the_download_moves_on()
    {
        var scheduler = Make(10 * MiB);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        var snapshot = scheduler.GetSegmentsSnapshot();

        scheduler.AdvanceLease(lease.LeaseId, 1000);

        snapshot.Single().Written.Should().Be(0);
        scheduler.GetSegmentsSnapshot().Single().Written.Should().Be(1000);
    }

    // ---- ownership, abandon, validity ------------------------------------------------------------------------------

    [Fact]
    public void A_segment_stays_with_its_worker_between_leases()
    {
        var scheduler = Make(100 * MiB);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        ReadFully(scheduler, first);

        var idleWorker = scheduler.TryAcquireNextLease(1)!.Value;   // must split, not take over worker 0's segment
        var next = scheduler.TryAcquireNextLease(0)!.Value;

        scheduler.StructureVersion.Should().Be(1);
        idleWorker.Start.Should().BeGreaterThan(first.End);
        (next.SegmentIndex, next.Start).Should().Be((0, first.End));
    }

    [Fact]
    public void An_abandoned_lease_keeps_the_bytes_received_and_releases_the_segment_for_any_worker()
    {
        var scheduler = Make(100 * MiB);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(first.LeaseId, 100_000);

        scheduler.AbandonLease(first.LeaseId);
        var taken = scheduler.TryAcquireNextLease(1)!.Value;

        scheduler.StructureVersion.Should().Be(0, "an abandoned segment is taken as it is, not split");
        (taken.SegmentIndex, taken.Start).Should().Be((0, 100_000L));
        scheduler.TotalWritten.Should().Be(100_000);
    }

    [Fact]
    public void The_worker_that_abandoned_gets_a_split_once_another_worker_owns_the_segment()
    {
        var scheduler = Make(100 * MiB);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AbandonLease(first.LeaseId);
        _ = scheduler.TryAcquireNextLease(1);

        var again = scheduler.TryAcquireNextLease(0)!.Value;

        again.SegmentIndex.Should().Be(1);
        scheduler.StructureVersion.Should().Be(1);
    }

    [Fact]
    public void A_worker_with_a_lease_gets_none_and_workers_outside_the_allowed_range_are_refused()
    {
        var scheduler = Make(100 * MiB, workers: 4, rangesSupported: false);
        _ = scheduler.TryAcquireNextLease(0);

        scheduler.TryAcquireNextLease(0).Should().BeNull("it already holds a lease");
        scheduler.TryAcquireNextLease(1).Should().BeNull("a server without ranges allows one connection");
        scheduler.AllowedConnections.Should().Be(1);
    }

    [Fact]
    public void Without_range_support_the_segment_is_never_split()
    {
        var scheduler = Make(100 * MiB, rangesSupported: false);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        ReadFully(scheduler, lease);

        scheduler.TryAcquireNextLease(0)!.Value.Start.Should().Be(lease.End);
        scheduler.StructureVersion.Should().Be(0);
        scheduler.GetSegmentsSnapshot().Should().ContainSingle();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void A_worker_id_outside_the_configured_range_is_a_programming_error(int worker)
    {
        var act = () => Make(10 * MiB).TryAcquireNextLease(worker);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Recording_bytes_for_an_unknown_lease_or_more_than_the_lease_holds_is_rejected()
    {
        var scheduler = Make(100 * MiB);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;

        var unknown = () => scheduler.AdvanceLease(999, 1);
        var tooMany = () => scheduler.AdvanceLease(lease.LeaseId, checked((int)lease.Length) + 1);
        var negative = () => scheduler.AdvanceLease(lease.LeaseId, -1);

        unknown.Should().Throw<InvalidOperationException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
        scheduler.TotalWritten.Should().Be(0);
    }

    [Fact]
    public void A_lease_that_was_not_read_to_its_end_cannot_be_completed_only_abandoned()
    {
        var scheduler = Make(100 * MiB);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(lease.LeaseId, 1000);

        var act = () => scheduler.CompleteLease(lease.LeaseId);

        act.Should().Throw<InvalidOperationException>();
        scheduler.AbandonLease(lease.LeaseId);
        scheduler.TryAcquireNextLease(0).Should().NotBeNull();
    }

    [Fact]
    public void Advancing_adds_to_the_totals_and_reports_the_bytes_that_were_new()
    {
        var scheduler = Make(100 * MiB);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;

        var a = scheduler.AdvanceLease(lease.LeaseId, 1000);
        var b = scheduler.AdvanceLease(lease.LeaseId, 500);

        (a.AcceptedStart, a.AcceptedLength, a.IsLeaseComplete).Should().Be((0L, 1000L, false));
        (b.AcceptedStart, b.AcceptedLength).Should().Be((1000L, 500L));
        scheduler.TotalWritten.Should().Be(1500);
        scheduler.GetSegmentsSnapshot().Single().Written.Should().Be(1500);
    }

    [Fact]
    public void The_download_is_complete_when_every_byte_is_received()
    {
        var scheduler = Make(700 * KiB);
        ReadFully(scheduler, scheduler.TryAcquireNextLease(0)!.Value);

        scheduler.IsComplete.Should().BeTrue();
        scheduler.TryAcquireNextLease(0).Should().BeNull();
        scheduler.TotalWritten.Should().Be(700 * KiB);
    }

    // ---- resume (restoring persisted segments) -------------------------------------------------------------------

    [Fact]
    public void Persisted_segments_are_restored_and_the_next_lease_starts_at_the_cursor()
    {
        var scheduler = new SegmentScheduler(
            3000 * KiB,
            [new Segment(0, 0, 1000 * KiB, 1000 * KiB), new Segment(1, 1000 * KiB, 3000 * KiB, 500 * KiB)],
            options: new SegmentSchedulerOptions { MaxConnections = 4 });

        var lease = scheduler.TryAcquireNextLease(0)!.Value;

        scheduler.TotalWritten.Should().Be(1500 * KiB);
        scheduler.IsComplete.Should().BeFalse();
        (lease.SegmentIndex, lease.Start).Should().Be((1, 1500 * KiB));
        scheduler.StructureVersion.Should().Be(0);
    }

    [Fact]
    public void Restoring_only_finished_segments_gives_a_complete_scheduler()
    {
        var scheduler = new SegmentScheduler(1000, [new Segment(0, 0, 1000, 1000)]);

        scheduler.IsComplete.Should().BeTrue();
        scheduler.TryAcquireNextLease(0).Should().BeNull();
    }

    [Fact]
    public void Restored_segment_indexes_continue_after_the_highest_one()
    {
        var scheduler = new SegmentScheduler(
            100 * MiB,
            [new Segment(5, 0, 50 * MiB), new Segment(2, 50 * MiB, 100 * MiB)],
            options: new SegmentSchedulerOptions { MaxConnections = 4 });
        _ = scheduler.TryAcquireNextLease(0);
        _ = scheduler.TryAcquireNextLease(1);

        _ = scheduler.TryAcquireNextLease(2);

        scheduler.GetSegmentsSnapshot().Select(s => s.Index).Should().Contain(6);
    }

    public static TheoryData<string, long, Segment[]> BadPartitions => new()
    {
        { "a gap", 3000, [new Segment(0, 0, 1000), new Segment(1, 1500, 3000)] },
        { "an overlap", 3000, [new Segment(0, 0, 1500), new Segment(1, 1000, 3000)] },
        { "the first segment not at zero", 3000, [new Segment(0, 100, 3000)] },
        { "the last segment short of the file", 3000, [new Segment(0, 0, 2000)] },
        { "the last segment past the file", 3000, [new Segment(0, 0, 4000)] },
        { "a repeated index", 3000, [new Segment(0, 0, 1000), new Segment(0, 1000, 3000)] },
        { "segments for an empty file", 0, [new Segment(0, 0, 10)] },
        { "no segments for a file with bytes", 3000, [] },
    };

    [Theory]
    [MemberData(nameof(BadPartitions))]
    public void Segments_that_do_not_partition_the_file_are_rejected(string reason, long total, Segment[] segments)
    {
        var act = () => new SegmentScheduler(total, segments);

        act.Should().Throw<ArgumentException>(reason);
    }

    // ---- whole downloads through the simulator ---------------------------------------------------------------------

    [Fact]
    public void Eight_equal_workers_download_one_GiB_receiving_every_byte_exactly_once()
    {
        var sim = new SchedulerSimulator(1 * GiB, Enumerable.Repeat(8.0 * MiB, 8).ToArray());

        sim.RunUntilComplete(TimeSpan.FromMinutes(10));

        sim.Scheduler.TotalWritten.Should().Be(1 * GiB);
        sim.BytesReceived.Should().Be(1 * GiB, "without endgame duplication nothing is fetched twice");
        sim.Scheduler.StructureVersion.Should().BeGreaterThan(0);
        sim.Scheduler.GetSegmentsSnapshot().Should().OnlyContain(s => s.IsComplete);
    }

    [Theory]
    [InlineData(4L * GiB + 1)]
    [InlineData(20L * GiB)]
    public void Files_beyond_four_GiB_are_partitioned_and_completed_without_overflow(long total)
    {
        var sim = new SchedulerSimulator(total, Enumerable.Repeat(1.0 * GiB, 8).ToArray());

        sim.RunUntilComplete(TimeSpan.FromHours(1));

        sim.Scheduler.TotalWritten.Should().Be(total);
        sim.Scheduler.GetSegmentsSnapshot()[^1].End.Should().Be(total);
    }

    [Fact]
    public void A_one_byte_file_is_one_lease()
    {
        var sim = new SchedulerSimulator(1, [MiB]);

        sim.RunUntilComplete(TimeSpan.FromSeconds(5));

        sim.Issued.Should().ContainSingle().Which.Length.Should().Be(1);
    }

    [Fact]
    public void A_failure_in_the_middle_of_a_download_loses_no_bytes_when_the_lease_is_abandoned_and_retried()
    {
        var scheduler = Make(10 * MiB);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(first.LeaseId, 200_000);
        scheduler.AbandonLease(first.LeaseId);

        while (!scheduler.IsComplete)
        {
            var lease = scheduler.TryAcquireNextLease(0)!.Value;
            scheduler.AdvanceLease(lease.LeaseId, checked((int)lease.Length));
            scheduler.CompleteLease(lease.LeaseId);
            scheduler.AssertInvariants();
        }

        scheduler.TotalWritten.Should().Be(10 * MiB);
    }
}
