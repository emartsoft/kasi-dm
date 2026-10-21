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
}
