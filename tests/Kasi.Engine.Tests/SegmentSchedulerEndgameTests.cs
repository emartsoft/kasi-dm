using Kasi.Core;
using Kasi.Engine.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-005")]
[Trait("TC", "TC-ENG-009")]
public class SegmentSchedulerEndgameTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;

    private static SegmentScheduler Restore(long total, Segment[] segments, int workers = 4, SegmentSchedulerOptions? options = null) =>
        new(
            total,
            segments,
            rangesSupported: true,
            options ?? new SegmentSchedulerOptions { MaxConnections = workers, InitialConnections = workers },
            new FakeTimeProvider());

    /// <summary>Two nearly finished segments, one worker on each, and two more workers with nothing left to split.</summary>
    private static (SegmentScheduler Scheduler, LeaseAssignment W0, LeaseAssignment W1) TwoOwners()
    {
        var scheduler = Restore(
            4 * MiB,
            [new Segment(0, 0, 2 * MiB, 1536 * KiB), new Segment(1, 2 * MiB, 4 * MiB, 1228 * KiB)]);
        return (scheduler, scheduler.TryAcquireNextLease(0)!.Value, scheduler.TryAcquireNextLease(1)!.Value);
    }

    [Fact]
    public void When_nothing_is_left_to_split_an_idle_worker_duplicates_the_slowest_unfinished_segment()
    {
        var (scheduler, w0, w1) = TwoOwners();
        (w0.Start, w0.End).Should().Be((1536 * KiB, 2048 * KiB));
        (w1.Start, w1.End).Should().Be((3276 * KiB, 4096 * KiB));

        var duplicate = scheduler.TryAcquireNextLease(2)!.Value;

        duplicate.IsEndgameDuplicate.Should().BeTrue();
        (duplicate.SegmentIndex, duplicate.Start, duplicate.End).Should().Be((1, 3276 * KiB, 4096 * KiB), "both owners are equally slow, so the segment with more left is shadowed");
        scheduler.EndgameDuplicateBytesAllocated.Should().Be(820 * KiB);
        scheduler.StructureVersion.Should().Be(0);
        scheduler.AssertInvariants();
    }

    [Fact]
    public void The_slowest_owner_is_the_one_that_gets_shadowed()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SegmentScheduler(
            4 * MiB,
            [new Segment(0, 0, 2 * MiB, 1536 * KiB), new Segment(1, 2 * MiB, 4 * MiB, 1228 * KiB)],
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            time);
        var w0 = scheduler.TryAcquireNextLease(0)!.Value;
        var w1 = scheduler.TryAcquireNextLease(1)!.Value;
        scheduler.AdvanceLease(w1.LeaseId, 100 * 1000);            // worker 1 moves 100 KB in the first second ...
        time.Advance(TimeSpan.FromSeconds(1));
        scheduler.AdvanceLease(w1.LeaseId, 1);
        scheduler.AdvanceLease(w0.LeaseId, 1000);                   // ... worker 0 only a trickle
        time.Advance(TimeSpan.FromSeconds(1));
        scheduler.AdvanceLease(w0.LeaseId, 1);
        scheduler.AdvanceLease(w1.LeaseId, 1);

        var duplicate = scheduler.TryAcquireNextLease(2)!.Value;

        duplicate.SegmentIndex.Should().Be(w0.SegmentIndex);
    }

    [Fact]
    public void Each_segment_is_shadowed_at_most_once_and_when_all_are_covered_the_next_worker_gets_nothing()
    {
        var (scheduler, _, _) = TwoOwners();

        var first = scheduler.TryAcquireNextLease(2)!.Value;
        var second = scheduler.TryAcquireNextLease(3)!.Value;

        (first.SegmentIndex, second.SegmentIndex).Should().Be((1, 0));
        scheduler.EndgameDuplicateBytesAllocated.Should().Be((820 + 512) * KiB);
    }

    [Fact]
    public void Duplicate_traffic_never_exceeds_the_2_MiB_budget()
    {
        var segments = Enumerable.Range(0, 4).Select(i => new Segment(i, i * 2 * MiB, (i + 1) * 2 * MiB, 1148 * KiB)).ToArray();   // 900 KiB left in each
        var scheduler = Restore(8 * MiB, segments, workers: 8);
        for (var worker = 0; worker < 4; worker++)
        {
            scheduler.TryAcquireNextLease(worker).Should().NotBeNull();
        }

        var granted = Enumerable.Range(4, 4).Select(scheduler.TryAcquireNextLease).ToArray();

        granted.Count(g => g is not null).Should().Be(2, "900 + 900 KiB fit in 2048 KiB, a third would not");
        scheduler.EndgameDuplicateBytesAllocated.Should().Be(1800 * KiB).And.BeLessThanOrEqualTo(2 * MiB);
    }

    [Fact]
    public void A_segment_with_more_left_than_the_budget_is_never_duplicated()
    {
        var scheduler = Restore(2_516_582, [new Segment(0, 0, 2_516_582)]);   // 2.4 MiB: one lease of 512 KiB leaves 1.9 MiB, too little to split
        _ = scheduler.TryAcquireNextLease(0);

        scheduler.TryAcquireNextLease(1).Should().BeNull();
        scheduler.EndgameDuplicateBytesAllocated.Should().Be(0);
    }

    [Fact]
    public void A_segment_of_one_to_two_MiB_behind_a_slow_worker_is_duplicated_instead_of_stalling_every_other_connection()
    {
        var scheduler = Restore(2 * MiB, [new Segment(0, 0, 2 * MiB, 512 * KiB)]);   // 1.5 MiB left: more than MinSplit, less than the budget
        _ = scheduler.TryAcquireNextLease(0);

        var duplicate = scheduler.TryAcquireNextLease(1)!.Value;

        (duplicate.IsEndgameDuplicate, duplicate.Start, duplicate.End).Should().Be((true, 512 * KiB, 2 * MiB));
        scheduler.EndgameDuplicateBytesAllocated.Should().Be(1536 * KiB);
    }

    [Fact]
    public void No_duplicate_is_made_while_a_segment_can_still_be_split_or_taken()
    {
        var scheduler = Restore(100 * MiB, [new Segment(0, 0, 100 * MiB)], workers: 2);
        _ = scheduler.TryAcquireNextLease(0);

        var next = scheduler.TryAcquireNextLease(1)!.Value;

        next.IsEndgameDuplicate.Should().BeFalse();
        scheduler.StructureVersion.Should().Be(1);
    }

    [Fact]
    public void Without_range_support_there_is_no_duplication()
    {
        var scheduler = new SegmentScheduler(
            2 * MiB,
            [new Segment(0, 0, 2 * MiB, 512 * KiB)],
            rangesSupported: false,
            new SegmentSchedulerOptions { MaxConnections = 4 },
            new FakeTimeProvider());
        _ = scheduler.TryAcquireNextLease(0);

        scheduler.TryAcquireNextLease(1).Should().BeNull();
    }

    [Fact]
    public void If_the_shadow_finishes_first_it_completes_the_segment_and_the_owner_is_told_to_stop()
    {
        var (scheduler, _, w1) = TwoOwners();
        var shadow = scheduler.TryAcquireNextLease(2)!.Value;
        var before = scheduler.TotalWritten;

        var win = scheduler.AdvanceLease(shadow.LeaseId, checked((int)shadow.Length));
        var late = scheduler.AdvanceLease(w1.LeaseId, 1000);
        scheduler.CompleteLease(w1.LeaseId);
        scheduler.CompleteLease(shadow.LeaseId);

        (win.AcceptedStart, win.AcceptedLength, win.IsLeaseComplete, win.IsSuperseded).Should().Be((shadow.Start, shadow.Length, true, false));
        (late.AcceptedLength, late.IsSuperseded).Should().Be((0L, true));
        scheduler.TotalWritten.Should().Be(before + shadow.Length, "the bytes of the segment are counted once");
        scheduler.GetSegmentsSnapshot().Single(s => s.Index == 1).IsComplete.Should().BeTrue();
        scheduler.TryAcquireNextLease(1).Should().NotBeNull("worker 1 is free again and the other segment can still be shadowed or finished");
        scheduler.AssertInvariants();
    }

    [Fact]
    public void If_the_owner_finishes_first_the_shadow_is_superseded()
    {
        var (scheduler, w0, _) = TwoOwners();
        _ = scheduler.TryAcquireNextLease(2);
        var shadow = scheduler.TryAcquireNextLease(3)!.Value;   // shadows segment 0
        shadow.SegmentIndex.Should().Be(0);

        var win = scheduler.AdvanceLease(w0.LeaseId, checked((int)w0.Length));
        var late = scheduler.AdvanceLease(shadow.LeaseId, 100);
        scheduler.CompleteLease(w0.LeaseId);
        scheduler.CompleteLease(shadow.LeaseId);

        win.IsLeaseComplete.Should().BeTrue();
        (late.IsSuperseded, late.AcceptedLength).Should().Be((true, 0L));
        scheduler.GetSegmentsSnapshot().Single(s => s.Index == 0).IsComplete.Should().BeTrue();
        scheduler.AssertInvariants();
    }

    [Fact]
    public void Both_leases_writing_the_same_bytes_advance_the_segment_only_by_the_furthest_of_them()
    {
        var (scheduler, _, w1) = TwoOwners();
        var shadow = scheduler.TryAcquireNextLease(2)!.Value;   // shadows segment 1, which worker 1 owns
        var written = scheduler.GetSegmentsSnapshot().Single(s => s.Index == 1).Written;

        var a = scheduler.AdvanceLease(shadow.LeaseId, 100 * 1000);   // 100 KB, all new
        var b = scheduler.AdvanceLease(shadow.LeaseId, 200 * 1000);   // 200 KB more, all new
        var owner = scheduler.AdvanceLease(w1.LeaseId, 150 * 1000);   // the owner is 150 KB behind the shadow: nothing new

        a.AcceptedLength.Should().Be(100 * 1000);
        b.AcceptedLength.Should().Be(200 * 1000);
        owner.AcceptedLength.Should().Be(0);
        scheduler.GetSegmentsSnapshot().Single(s => s.Index == 1).Written.Should().Be(written + (300 * 1000));
    }

    [Fact]
    public void An_owner_that_falls_behind_the_shadow_does_not_double_count_when_it_catches_up()
    {
        var (scheduler, _, w1) = TwoOwners();
        var shadow = scheduler.TryAcquireNextLease(2)!.Value;
        var before = scheduler.TotalWritten;

        scheduler.AdvanceLease(shadow.LeaseId, 400 * 1000);
        var behind = scheduler.AdvanceLease(w1.LeaseId, 300 * 1000);     // the owner is 100 KB behind the shadow
        var catchUp = scheduler.AdvanceLease(w1.LeaseId, 150 * 1000);    // and now 50 KB ahead of it

        behind.AcceptedLength.Should().Be(0);
        catchUp.AcceptedLength.Should().Be(50 * 1000);
        scheduler.TotalWritten.Should().Be(before + (450 * 1000));
    }

    [Fact]
    public void When_the_owner_fails_the_shadow_takes_the_segment_over()
    {
        var (scheduler, _, w1) = TwoOwners();
        var shadow = scheduler.TryAcquireNextLease(2)!.Value;

        scheduler.AbandonLease(w1.LeaseId);
        scheduler.AssertInvariants();
        var result = scheduler.AdvanceLease(shadow.LeaseId, checked((int)shadow.Length));
        scheduler.CompleteLease(shadow.LeaseId);

        result.IsLeaseComplete.Should().BeTrue();
        scheduler.GetSegmentsSnapshot().Single(s => s.Index == 1).IsComplete.Should().BeTrue();
        scheduler.AssertInvariants();
    }

    [Fact]
    public void A_failed_shadow_leaves_the_owner_untouched_and_the_segment_can_be_shadowed_again_within_the_budget()
    {
        var (scheduler, _, w1) = TwoOwners();
        var shadow = scheduler.TryAcquireNextLease(2)!.Value;

        scheduler.AbandonLease(shadow.LeaseId);
        scheduler.AssertInvariants();
        var again = scheduler.TryAcquireNextLease(2)!.Value;

        again.IsEndgameDuplicate.Should().BeTrue();
        scheduler.AdvanceLease(w1.LeaseId, 1000).IsSuperseded.Should().BeFalse();
    }

    [Fact]
    [Trait("TC", "TC-ENG-009")]
    public void A_very_slow_owner_of_the_last_segment_is_overtaken_by_a_duplicate_instead_of_holding_the_download_up()
    {
        var sim = new SchedulerSimulator(2_600 * KiB, [40_000, 5_000_000]);

        sim.RunUntilComplete(TimeSpan.FromMinutes(5));

        (sim.Time.GetUtcNow() - new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)).Should().BeLessThan(TimeSpan.FromSeconds(5), "the slow worker alone would need over 30 s for its share");
        sim.Issued.Should().Contain(l => l.IsEndgameDuplicate);
        sim.Scheduler.TotalWritten.Should().Be(2_600 * KiB);
        sim.Scheduler.EndgameDuplicateBytesAllocated.Should().BeInRange(1, 2 * MiB);
    }

    [Fact]
    public void Across_a_whole_download_the_duplicated_bytes_stay_within_the_budget_and_every_byte_arrives()
    {
        var sim = new SchedulerSimulator(20 * MiB, [200_000, .. Enumerable.Repeat(5_000_000.0, 7)]);

        sim.RunUntilComplete(TimeSpan.FromMinutes(5));

        sim.Scheduler.TotalWritten.Should().Be(20 * MiB);
        sim.Scheduler.EndgameDuplicateBytesAllocated.Should().BeLessThanOrEqualTo(2 * MiB);
        (sim.BytesReceived - (20 * MiB)).Should().BeInRange(0, (2 * MiB) + (100 * KiB), "duplicates plus at most one tick of a superseded owner");
    }

    // ---- tail lease shrink (option) ---------------------------------------------------------------------------------

    private static LeaseAssignment SecondLease(bool shrink)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SegmentScheduler(
            100 * MiB,
            [new Segment(0, 0, 100 * MiB, 90 * MiB)],
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4, LeaseTargetDuration = TimeSpan.FromSeconds(100), ShrinkTailLeases = shrink },
            time);
        var first = scheduler.TryAcquireNextLease(0)!.Value;
        time.Advance(TimeSpan.FromSeconds(1));
        scheduler.AdvanceLease(first.LeaseId, 400 * 1000);
        time.Advance(TimeSpan.FromSeconds(1));
        scheduler.AdvanceLease(first.LeaseId, checked((int)first.Length) - (400 * 1000));
        scheduler.CompleteLease(first.LeaseId);
        return scheduler.TryAcquireNextLease(0)!.Value;
    }

    [Fact]
    public void Without_the_option_the_last_stretch_goes_out_as_one_big_lease()
    {
        // 9.5 MiB are left, less than the 16 MiB cap, so the whole stretch is one lease
        SecondLease(shrink: false).Length.Should().Be(100 * MiB - (90 * MiB + (512 * KiB)));
    }

    [Fact]
    public void With_the_option_the_last_stretch_is_shared_between_the_connections()
    {
        var lease = SecondLease(shrink: true);

        // 9.5 MiB are left after the first lease and four connections may transfer
        lease.Length.Should().Be((100 * MiB - (90 * MiB + (512 * KiB))) / 4);
    }
}
