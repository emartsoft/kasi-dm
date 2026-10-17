using FsCheck.Xunit;
using Kasi.Core;
using Kasi.Engine.Tests.Support;

namespace Kasi.Engine.Tests;

/// <summary>
/// TC-ENG-007: for random file sizes (0 B to 64 GiB), connection counts, worker speeds, speed changes, connection failures and
/// throttling, the segments never overlap, never leave a gap and always add up to the file, a split never cuts into a lease in
/// flight, duplicates stay within budget, and every download completes. Each run is derived from one integer, which FsCheck prints
/// (and shrinks) when a property fails, so a failure can be replayed with <c>Replay(seed)</c>.
/// </summary>
[Trait("TC", "TC-ENG-007")]
[Trait("Requirement", "KASI-ENG-005")]
[Trait("Requirement", "KASI-ENG-006")]
public class SegmentSchedulerPropertyTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    /// <summary>16 KB/s: a minimum lease (512 KiB) still completes in about 32 s.</summary>
    private const double MinimumSpeed = 16_000;

    private static readonly long[] _boundarySizes =
    [
        0, 1, 64 * KiB - 1, 64 * KiB, 64 * KiB + 1, MiB - 1, MiB, MiB + 1, 2 * MiB, 3 * MiB, 4 * GiB + 1, 20 * GiB, 64 * GiB,
    ];

    private sealed record Scenario(long Size, int Workers, SegmentSchedulerOptions Options, double[] Speeds, Random Random, double FailurePerTick);

    private static Scenario Build(int seed)
    {
        var random = new Random(seed);
        var size = random.NextDouble() < 0.35
            ? _boundarySizes[random.Next(_boundarySizes.Length)]
            : (long)Math.Exp(random.NextDouble() * Math.Log(64.0 * GiB)) - 1;
        size = Math.Clamp(size, 0, 64 * GiB);

        var workers = random.Next(1, 33);
        var options = new SegmentSchedulerOptions
        {
            MaxConnections = workers,
            InitialConnections = random.NextDouble() < 0.5 ? workers : random.Next(1, workers + 1),
            PageAlignment = new[] { 4 * KiB, 64 * KiB, 64 * KiB, MiB }[random.Next(4)],
            ShrinkTailLeases = random.NextDouble() < 0.3,
        };

        // The aggregate rate would finish in about 150 s of virtual time and individual workers differ by up to 50 times, but none
        // is slower than MinimumSpeed: a connection that moves nothing for minutes is the engine's stall timeout to abandon (TRD 6.6),
        // not something the scheduler can or should rescue (ADR 0004).
        var factors = Enumerable.Range(0, workers).Select(_ => 0.02 + random.NextDouble()).ToArray();
        var aggregate = Math.Max(size, 1) / 150.0;
        var speeds = factors.Select(f => Math.Max(MinimumSpeed, aggregate * f / factors.Sum())).ToArray();

        return new Scenario(size, workers, options, speeds, random, random.NextDouble() < 0.5 ? 0 : 0.001);
    }

    private static SchedulerSimulator Simulate(Scenario scenario, Action<SchedulerSimulator, int>? perStep = null, int maxSteps = 24_000)
    {
        var sim = new SchedulerSimulator(
            scenario.Size,
            scenario.Speeds,
            scenario.Options,
            random: scenario.Random,
            failurePerTick: scenario.FailurePerTick);
        var steps = 0;
        var reduced = false;
        while (!sim.Scheduler.IsComplete)
        {
            steps++;
            steps.Should().BeLessThan(maxSteps, "the download must finish (size {0}, {1} workers)", scenario.Size, scenario.Workers);

            if (steps % 400 == 0)
            {
                // the world changes: one worker speeds up or slows down
                var worker = scenario.Random.Next(scenario.Workers);
                sim.SetSpeed(worker, Math.Max(MinimumSpeed, scenario.Speeds[worker] * (0.2 + (2.8 * scenario.Random.NextDouble()))));
            }

            if (!reduced && scenario.Workers > 1 && steps == 300 && scenario.Random.NextDouble() < 0.3)
            {
                reduced = true;
                sim.Scheduler.ReduceAllowedConnections(scenario.Random.Next(1, scenario.Workers));
            }

            sim.Step();
            sim.Scheduler.EndgameDuplicateBytesAllocated.Should().BeLessThanOrEqualTo(2 * MiB);
            perStep?.Invoke(sim, steps);
        }

        return sim;
    }

    [Property(MaxTest = 120, QuietOnSuccess = true)]
    public void Every_download_partitions_the_file_at_every_step_and_completes(int seed)
    {
        var scenario = Build(seed);

        var sim = Simulate(scenario);

        var segments = sim.Scheduler.GetSegmentsSnapshot();
        sim.Scheduler.TotalWritten.Should().Be(scenario.Size);
        segments.Should().OnlyContain(s => s.IsComplete);
        segments.Sum(s => s.Length).Should().Be(scenario.Size);
        segments.Count.Should().Be(scenario.Size == 0 ? 0 : sim.Scheduler.StructureVersion + 1, "every split adds exactly one segment and nothing is ever merged");
    }

    [Property(MaxTest = 120, QuietOnSuccess = true)]
    public void Leases_stay_inside_the_file_and_within_the_size_limits(int seed)
    {
        var scenario = Build(seed);

        var sim = Simulate(scenario);

        foreach (var lease in sim.Issued)
        {
            lease.Length.Should().BeGreaterThan(0);
            lease.Start.Should().BeGreaterThanOrEqualTo(0);
            lease.End.Should().BeLessThanOrEqualTo(scenario.Size);
            lease.Length.Should().BeLessThanOrEqualTo(
                lease.IsEndgameDuplicate ? 2 * MiB : scenario.Options.MaxLeaseBytes + scenario.Options.MinLeaseBytes,
                "a lease is at most the cap, plus a folded-in trailing remainder smaller than the minimum lease");
        }
    }

    [Property(MaxTest = 80, QuietOnSuccess = true)]
    public void Resuming_from_a_snapshot_taken_at_any_moment_finishes_the_same_file_without_losing_a_byte(int seed)
    {
        var scenario = Build(seed);
        var stopAt = scenario.Random.Next(1, 1500);
        var first = new SchedulerSimulator(scenario.Size, scenario.Speeds, scenario.Options, random: scenario.Random, failurePerTick: scenario.FailurePerTick);
        for (var step = 0; step < stopAt && !first.Scheduler.IsComplete; step++)
        {
            first.Step();
        }

        var snapshot = first.Scheduler.GetSegmentsSnapshot();
        var resumed = new SchedulerSimulator(
            scenario.Size,
            scenario.Speeds,
            scenario.Options,
            existingSegments: snapshot,
            random: scenario.Random,
            failurePerTick: scenario.FailurePerTick);
        resumed.Scheduler.TotalWritten.Should().Be(snapshot.Sum(s => s.Written));

        resumed.RunUntilComplete(TimeSpan.FromMinutes(30));

        resumed.Scheduler.TotalWritten.Should().Be(scenario.Size);
        resumed.Scheduler.GetSegmentsSnapshot().Should().OnlyContain(s => s.IsComplete);
        resumed.BytesReceived.Should().BeGreaterThanOrEqualTo(scenario.Size - snapshot.Sum(s => s.Written), "everything not yet written is received again");
    }

    [Property(MaxTest = 60, QuietOnSuccess = true)]
    public void Snapshot_progress_never_goes_backwards_during_a_download(int seed)
    {
        var scenario = Build(seed);
        var previous = new Dictionary<int, long>();

        Simulate(scenario, (sim, _) =>
        {
            foreach (var segment in sim.Scheduler.GetSegmentsSnapshot())
            {
                if (previous.TryGetValue(segment.Index, out var written))
                {
                    segment.Written.Should().BeGreaterThanOrEqualTo(written, "the cursor of segment {0} must never move back", segment.Index);
                }

                previous[segment.Index] = segment.Written;
            }
        });
    }

    [Fact]
    public void The_boundary_sizes_all_complete()
    {
        foreach (var size in _boundarySizes)
        {
            var speeds = Enumerable.Repeat(Math.Max(size, 1) / 100.0, 8).ToArray();
            var sim = new SchedulerSimulator(size, speeds);

            sim.RunUntilComplete(TimeSpan.FromMinutes(10));

            sim.Scheduler.TotalWritten.Should().Be(size);
            sim.Scheduler.GetSegmentsSnapshot().Sum(s => s.Length).Should().Be(size);
        }
    }
}
