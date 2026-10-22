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
}
