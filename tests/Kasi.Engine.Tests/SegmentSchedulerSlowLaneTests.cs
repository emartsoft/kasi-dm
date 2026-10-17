using Kasi.Core;
using Kasi.Engine.Tests.Support;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-005")]
public class SegmentSchedulerSlowLaneTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    private static SchedulerSimulator Sim(long total, params double[] speeds) =>
        new(total, speeds, new SegmentSchedulerOptions { MaxConnections = speeds.Length, InitialConnections = speeds.Length });

    private static void RunFor(SchedulerSimulator sim, TimeSpan duration)
    {
        var end = sim.Time.GetUtcNow() + duration;
        while (sim.Time.GetUtcNow() < end && !sim.Scheduler.IsComplete)
        {
            sim.Step();
        }
    }

    /// <summary>The bytes of each incomplete segment that no lease covers yet; the simulator knows which leases are in flight.</summary>
    private static IEnumerable<long> UnleasedBytes(SchedulerSimulator sim, int workers)
    {
        var leases = Enumerable.Range(0, workers).Select(sim.CurrentLease).Where(l => l is not null).Select(l => l!.Value).ToList();
        foreach (var segment in sim.Scheduler.GetSegmentsSnapshot().Where(s => !s.IsComplete))
        {
            var leaseEnd = leases.Where(l => l.SegmentIndex == segment.Index).Select(l => l.End).DefaultIfEmpty(0).Max();
            yield return segment.End - Math.Max(segment.Cursor, leaseEnd);
        }
    }

    [Fact]
    public void A_worker_below_30_percent_of_the_median_for_10_s_has_the_rest_of_its_segment_cut_off_for_the_next_free_worker()
    {
        var sim = Sim(32 * MiB, 10_000, 400_000, 400_000, 400_000);
        RunFor(sim, TimeSpan.FromSeconds(10.5));
        sim.Scheduler.StructureVersion.Should().Be(3, "only the three splits of the start so far: 10 s of slowness have not passed");

        RunFor(sim, TimeSpan.FromSeconds(2));

        sim.Scheduler.StructureVersion.Should().Be(4, "the slow worker's tail was carved off once");
        var slowLease = sim.CurrentLease(0)!.Value;
        var snapshot = sim.Scheduler.GetSegmentsSnapshot();
        var victim = snapshot.Single(s => s.Index == slowLease.SegmentIndex);
        var carved = snapshot.Single(s => s.Index == 4);
        carved.Start.Should().Be(victim.End);
        carved.Written.Should().Be(0);
        victim.End.Should().BeGreaterThanOrEqualTo(slowLease.End, "the lease in flight is never cut into");
        (victim.End - slowLease.End).Should().BeLessThanOrEqualTo(64 * KiB, "the slow worker keeps only the lease it is reading");
        (carved.Length).Should().BeGreaterThan(4 * MiB);
    }

    [Fact]
    public void The_carved_tail_goes_to_the_first_worker_that_becomes_free_and_the_download_completes()
    {
        var sim = Sim(32 * MiB, 10_000, 400_000, 400_000, 400_000);
        RunFor(sim, TimeSpan.FromSeconds(12.5));
        var issuedBefore = sim.Issued.Count;

        sim.RunUntilComplete(TimeSpan.FromMinutes(10));

        var taken = sim.Issued.Skip(issuedBefore).First(l => l.SegmentIndex == 4);
        taken.WorkerId.Should().NotBe(0, "the carved tail goes to a fast worker once its own segment is finished");
        sim.Scheduler.TotalWritten.Should().Be(32 * MiB);
    }

    [Fact]
    public void A_worker_that_recovers_before_10_s_is_left_alone()
    {
        var sim = Sim(32 * MiB, 10_000, 400_000, 400_000, 400_000);
        RunFor(sim, TimeSpan.FromSeconds(8));
        sim.SetSpeed(0, 400_000);

        RunFor(sim, TimeSpan.FromSeconds(10));   // to 18 s: at about 20 s the fast workers finish their segments and a normal split follows

        sim.Scheduler.StructureVersion.Should().Be(3);
    }

    [Fact]
    public void A_segment_with_4_MiB_or_less_left_is_not_worth_taking_over()
    {
        var sim = Sim(12 * MiB, 10_000, 200_000, 200_000, 200_000);   // about 3 MiB per segment

        RunFor(sim, TimeSpan.FromSeconds(13));

        sim.Scheduler.StructureVersion.Should().Be(3);
    }

    [Fact]
    public void With_fewer_than_three_active_workers_there_is_no_median_to_be_slow_against()
    {
        var sim = Sim(64 * MiB, 10_000, 400_000);

        RunFor(sim, TimeSpan.FromSeconds(40));

        sim.Scheduler.StructureVersion.Should().Be(1);
    }

    [Fact]
    [Trait("TC", "TC-ENG-008")]
    public void One_of_eight_connections_at_50_KB_per_s_is_taken_over_after_10_s_and_the_others_never_idle_while_more_than_MinSplit_is_left()
    {
        var sim = Sim(1 * GiB, [50_000, .. Enumerable.Repeat(5_000_000.0, 7)]);
        var start = sim.Time.GetUtcNow();
        TimeSpan? carvedAt = null;
        var versionAfterStart = -1;

        while (!sim.Scheduler.IsComplete)
        {
            (sim.Time.GetUtcNow() - start).Should().BeLessThan(TimeSpan.FromSeconds(120), "without the takeover the slow worker alone would need over 40 minutes");
            var idleBefore = (int[])sim.IdleTicks.Clone();
            sim.Step();
            var elapsed = sim.Time.GetUtcNow() - start;

            if (versionAfterStart < 0)
            {
                versionAfterStart = sim.Scheduler.StructureVersion; // the splits of the first tick, when every worker asked at once
            }
            else if (carvedAt is null && sim.Scheduler.StructureVersion > versionAfterStart)
            {
                carvedAt = elapsed;
            }

            for (var worker = 0; worker < 8; worker++)
            {
                if (sim.IdleTicks[worker] > idleBefore[worker])
                {
                    UnleasedBytes(sim, 8).Should().OnlyContain(free => free < 2 * MiB, "worker {0} idled at {1} while a segment could still have been split", worker, elapsed);
                }
            }
        }

        carvedAt.Should().NotBeNull();
        carvedAt!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(11)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(12), "10 s after the first speed samples (1 s) plus one scheduler tick");
        sim.Scheduler.TotalWritten.Should().Be(1 * GiB);
        sim.Scheduler.GetSegmentsSnapshot().Should().OnlyContain(s => s.IsComplete);
    }
}
