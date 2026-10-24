namespace Kasi.FaultInjection.Tests;

[Trait("TC", "TC-RES-001")]
[Trait("Requirement", "KASI-RES-001")]
public sealed class RefetchBoundTests
{
    private const long MiB = 1024 * 1024;

    private static readonly FaultInjectionOptions _options = new() { Scenario = "kill", Rate = 2 * MiB, Connections = 4 };

    [Fact]
    public void The_strict_bound_is_two_seconds_of_data_plus_the_socket_buffers()
    {
        FaultInjectionRunner.StrictRefetchBound(_options).Should().Be((2 * 2 * MiB) + (4 * MiB));
    }

    [Fact]
    public void An_instant_kill_gets_the_strict_bound_plus_half_a_second_of_jitter()
    {
        FaultInjectionRunner.RefetchBound(_options, TimeSpan.Zero).Should().Be(FaultInjectionRunner.StrictRefetchBound(_options) + MiB);
    }

    [Fact]
    public void The_bound_grows_by_the_data_the_child_downloaded_while_the_kill_was_landing()
    {
        var quick = FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(0.1));
        var slow = FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(2.1));

        (slow - quick).Should().Be(2 * 2 * MiB, "two more seconds of a 2 MiB/s download");
    }

    [Fact]
    public void Three_seconds_of_lost_data_fails_a_quick_kill_and_passes_a_kill_that_took_two_seconds()
    {
        // the CI case that motivated this: 11.7 MiB fetched again at 2 MiB/s with four connections
        var lost = (long)(11.7 * MiB);

        lost.Should().BeGreaterThan(FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(0.1)), "an instant kill may not lose that much");
        lost.Should().BeLessThanOrEqualTo(FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(2)), "the child ran on for two seconds before it was gone");
    }

    [Fact]
    public void A_slow_checkpoint_commit_adds_the_data_downloaded_while_it_ran()
    {
        var without = FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(0.1));
        var slow = FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(1));

        (slow - without).Should().Be(2 * MiB, "one more second of a 2 MiB/s download");
    }

    [Fact]
    public void A_resume_that_starts_over_still_fails_with_a_slow_commit_allowed()
    {
        var bound = FaultInjectionRunner.RefetchBound(_options, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        (60 * 2 * MiB).Should().BeGreaterThan(bound, "a minute of data lost is not a slow flush");
    }

    [Fact]
    public void The_flush_latency_file_is_read_and_a_missing_or_partial_one_counts_as_zero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flush-{Guid.NewGuid():N}.latency");
        FaultInjectionRunner.ReadFlushLatency(path).Should().Be(TimeSpan.Zero);
        try
        {
            File.WriteAllText(path, "0.75");
            FaultInjectionRunner.ReadFlushLatency(path).Should().Be(TimeSpan.FromSeconds(0.75));
            File.WriteAllText(path, "");
            FaultInjectionRunner.ReadFlushLatency(path).Should().Be(TimeSpan.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_checkpoint_still_running_at_the_kill_is_counted_with_the_step_it_is_in()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inflight-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 10, 24, 1, 0, 30, TimeSpan.Zero);
        long Ticks(int secondsAgo) => now.AddSeconds(-secondsAgo).UtcTicks;
        try
        {
            File.WriteAllText(path, $"flush:{Ticks(7)}");
            FaultInjectionRunner.ReadInFlight(path, now).Should().Be(("flush", TimeSpan.FromSeconds(7)));

            File.WriteAllText(path, $"commit:{Ticks(9)}:{Ticks(2)}");
            FaultInjectionRunner.ReadInFlight(path, now).Should().Be(("commit", TimeSpan.FromSeconds(9)), "the time counts from the start of the checkpoint, not of the commit");

            File.WriteAllText(path, "0");
            FaultInjectionRunner.ReadInFlight(path, now).Should().Be(((string?)null, TimeSpan.Zero), "between checkpoints nothing is running");
            FaultInjectionRunner.ReadInFlight(path + ".missing", now).Should().Be(((string?)null, TimeSpan.Zero));
            File.WriteAllText(path, "commit:12"); // a write cut short by the kill
            FaultInjectionRunner.ReadInFlight(path, now).Phase.Should().Be("commit");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_checkpoint_may_take_at_most_sixty_seconds_whatever_the_bound_allows()
    {
        FaultInjectionRunner.CeilingProblem(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), "flush", 1).Should().BeNull("exactly at the ceiling is allowed");
        FaultInjectionRunner.CeilingProblem(TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(61), "flush", 5).Should().Contain("unfinished at the kill, in the flush step").And.Contain("60 s ceiling");
        FaultInjectionRunner.CeilingProblem(TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(2), null, 5).Should().Contain("completed");
        FaultInjectionRunner.RefetchBound(_options, TimeSpan.Zero, TimeSpan.FromSeconds(300)).Should().BeGreaterThan(300 * 2 * MiB, "the bound alone would let it pass");
    }

    [Fact]
    public void The_summary_counts_stalls_by_step()
    {
        FaultIterationResult Kill(int i, double inFlight, string? phase) => new(i, true, null, 0, 0, i + 1, 0, inFlight, 0, inFlight, 10, phase);
        var report = new FaultInjectionReport("kill", 1, 4, 4, 4, 0, 0, 0, true, null, TimeSpan.Zero, null, [Kill(0, 0, null), Kill(1, 6, "flush"), Kill(2, 23.3, "flush"), Kill(3, 5, "commit")]);

        report.ToMarkdown().Should().Contain("3 of 4").And.Contain("longest 23.3 s, in the flush step (2 in the part-file flush, 1 in the database commit)");
    }
}
