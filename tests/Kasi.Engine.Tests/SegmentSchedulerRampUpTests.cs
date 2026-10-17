using Kasi.Engine.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-004")]
public class SegmentSchedulerRampUpTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    private static SchedulerSimulator Sim(long total, int maxConnections, double perConnection = 4.0 * MiB, bool rangesSupported = true) =>
        new(
            total,
            Enumerable.Repeat(perConnection, maxConnections).ToArray(),
            new SegmentSchedulerOptions { MaxConnections = maxConnections },
            rangesSupported);

    [Fact]
    public void A_download_starts_with_one_connection()
    {
        var sim = Sim(1 * GiB, 8);

        sim.Scheduler.AllowedConnections.Should().Be(1);
        sim.Scheduler.TryAcquireNextLease(1).Should().BeNull();
        sim.Scheduler.TryAcquireNextLease(0).Should().NotBeNull();
    }

    [Fact]
    public void The_second_connection_is_added_after_the_first_250_ms_of_transfer()
    {
        var sim = Sim(1 * GiB, 8);
        var start = sim.Time.GetUtcNow();
        TimeSpan? addedAt = null;

        for (var step = 0; step < 20 && addedAt is null; step++)
        {
            var before = sim.Time.GetUtcNow() - start;
            sim.Step();
            if (sim.Scheduler.AllowedConnections == 2)
            {
                addedAt = before;
            }
        }

        addedAt.Should().NotBeNull();
        addedAt!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(300));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [Trait("TC", "TC-ENG-006")]
    public void On_a_link_that_scales_the_configured_connection_count_is_reached_no_faster_than_one_per_250_ms_and_the_file_completes(int max)
    {
        var sim = Sim(1 * GiB, max);
        var start = sim.Time.GetUtcNow();
        TimeSpan? reachedAt = null;
        var highest = 1;

        while (!sim.Scheduler.IsComplete)
        {
            (sim.Time.GetUtcNow() - start).Should().BeLessThan(TimeSpan.FromMinutes(10));
            sim.Step();
            var allowed = sim.Scheduler.AllowedConnections;
            highest = Math.Max(highest, allowed);
            if (allowed == max && reachedAt is null)
            {
                reachedAt = sim.Time.GetUtcNow() - start;
            }
        }

        highest.Should().Be(max);
        if (max > 1)
        {
            reachedAt!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250 * (max - 1)));
            reachedAt.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(400 * (max - 1)));
        }

        sim.Scheduler.TotalWritten.Should().Be(1 * GiB);
        sim.Scheduler.GetSegmentsSnapshot().Should().OnlyContain(s => s.IsComplete);
    }

    [Fact]
    public void Ramp_up_stops_when_the_link_is_saturated_and_does_not_start_again()
    {
        const double capacity = 16.0 * MiB;
        const double perConnection = 4.0 * MiB;
        var sim = Sim(4 * GiB, 8);

        for (var step = 0; step < 200; step++)
        {
            var allowed = sim.Scheduler.AllowedConnections;
            for (var worker = 0; worker < 8; worker++)
            {
                sim.SetSpeed(worker, Math.Min(perConnection, capacity / allowed));
            }

            sim.Step();
        }

        var settled = sim.Scheduler.AllowedConnections;

        settled.Should().BeInRange(4, 6, "the link carries 16 MiB/s, which four 4 MiB/s connections already fill");
        for (var step = 0; step < 100; step++)
        {
            for (var worker = 0; worker < 8; worker++)
            {
                sim.SetSpeed(worker, Math.Min(perConnection, capacity / settled));
            }

            sim.Step();
        }

        sim.Scheduler.AllowedConnections.Should().Be(settled);
    }

    [Fact]
    public void Ramp_up_survives_noisy_throughput_on_a_link_that_scales()
    {
        const int seed = 7; // printed so a failure can be replayed
        var random = new Random(seed);
        var sim = Sim(4 * GiB, 8);

        for (var step = 0; step < 200 && sim.Scheduler.AllowedConnections < 8; step++)
        {
            for (var worker = 0; worker < 8; worker++)
            {
                sim.SetSpeed(worker, 4.0 * MiB * (0.8 + (0.4 * random.NextDouble())));
            }

            sim.Step();
        }

        sim.Scheduler.AllowedConnections.Should().Be(8, "noise of +-20% per tick must not read as a plateau (seed {0})", seed);
    }

    [Fact]
    public void No_connection_is_added_while_there_is_nothing_for_it_to_fetch()
    {
        var sim = Sim(600 * KiB, 8);

        sim.RunUntilComplete(TimeSpan.FromSeconds(30));

        sim.Scheduler.AllowedConnections.Should().Be(1);
        sim.Issued.Select(l => l.WorkerId).Distinct().Should().Equal(0);
    }

    [Fact]
    public void A_server_without_range_support_never_ramps_up()
    {
        var sim = Sim(64 * MiB, 8, rangesSupported: false);

        sim.RunUntilComplete(TimeSpan.FromMinutes(5));

        sim.Scheduler.AllowedConnections.Should().Be(1);
        sim.Scheduler.StructureVersion.Should().Be(0);
    }

    [Fact]
    public void Ramp_up_waits_for_the_newest_connection_to_start_before_judging_it()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SegmentScheduler(
            1 * GiB,
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 4 },
            time);
        var lease = scheduler.TryAcquireNextLease(0)!.Value;

        scheduler.AdvanceLease(lease.LeaseId, 100_000);
        time.Advance(TimeSpan.FromMilliseconds(250));
        scheduler.AdvanceLease(lease.LeaseId, 100_000);
        scheduler.AllowedConnections.Should().Be(2);

        time.Advance(TimeSpan.FromSeconds(5));   // worker 1 never starts
        scheduler.AdvanceLease(lease.LeaseId, 100_000);

        scheduler.EvaluateRampUp().Should().Be(2);
    }

    [Fact]
    public void Throttling_lowers_the_limit_stops_the_ramp_up_and_releases_the_segments_of_workers_above_it()
    {
        var scheduler = new SegmentScheduler(
            1 * GiB,
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            new FakeTimeProvider());
        var leases = Enumerable.Range(0, 4).Select(w => scheduler.TryAcquireNextLease(w)!.Value).ToArray();
        var versionBefore = scheduler.StructureVersion;

        scheduler.ReduceAllowedConnections(2);
        scheduler.AllowedConnections.Should().Be(2);

        // worker 3 reads its lease to the end: it gets nothing more and its segment goes back to the pool
        scheduler.AdvanceLease(leases[3].LeaseId, checked((int)leases[3].Length));
        scheduler.CompleteLease(leases[3].LeaseId);
        scheduler.TryAcquireNextLease(3).Should().BeNull();
        scheduler.AdvanceLease(leases[0].LeaseId, checked((int)leases[0].Length));
        scheduler.CompleteLease(leases[0].LeaseId);
        var taken = scheduler.TryAcquireNextLease(0)!.Value;   // own segment first
        scheduler.AdvanceLease(leases[1].LeaseId, checked((int)leases[1].Length));
        scheduler.CompleteLease(leases[1].LeaseId);
        var released = scheduler.TryAcquireNextLease(1)!.Value;

        taken.SegmentIndex.Should().Be(leases[0].SegmentIndex);
        released.SegmentIndex.Should().Be(leases[1].SegmentIndex);
        scheduler.StructureVersion.Should().Be(versionBefore);
        scheduler.EvaluateRampUp().Should().Be(2);
    }

    [Fact]
    public void The_limit_never_drops_below_one_and_a_released_segment_is_picked_up_without_splitting()
    {
        var scheduler = new SegmentScheduler(
            1 * GiB,
            rangesSupported: true,
            new SegmentSchedulerOptions { MaxConnections = 3, InitialConnections = 3 },
            new FakeTimeProvider());
        var leases = Enumerable.Range(0, 3).Select(w => scheduler.TryAcquireNextLease(w)!.Value).ToArray();

        scheduler.ReduceAllowedConnections(100);
        scheduler.AdvanceLease(leases[2].LeaseId, checked((int)leases[2].Length));
        scheduler.CompleteLease(leases[2].LeaseId);
        scheduler.AdvanceLease(leases[0].LeaseId, checked((int)leases[0].Length));
        scheduler.CompleteLease(leases[0].LeaseId);
        scheduler.AbandonLease(leases[1].LeaseId);   // worker 1 fails: its segment is free for worker 0

        scheduler.AllowedConnections.Should().Be(1);
        var next = scheduler.TryAcquireNextLease(0)!.Value;

        next.SegmentIndex.Should().BeOneOf(leases[0].SegmentIndex, leases[1].SegmentIndex, leases[2].SegmentIndex);
        scheduler.AssertInvariants();
    }

    [Fact]
    public void Reducing_by_zero_or_a_negative_count_is_a_programming_error()
    {
        var scheduler = new SegmentScheduler(1 * MiB);

        var zero = () => scheduler.ReduceAllowedConnections(0);
        var negative = () => scheduler.ReduceAllowedConnections(-1);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
