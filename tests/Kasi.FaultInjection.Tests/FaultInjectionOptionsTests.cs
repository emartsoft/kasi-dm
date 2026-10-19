using Kasi.FaultInjection;

namespace Kasi.FaultInjection.Tests;

public sealed class FaultInjectionOptionsTests
{
    [Fact]
    public void Each_suite_has_its_own_default_count_and_a_value_the_caller_passed_is_never_replaced()
    {
        FaultInjectionOptions.Parse([]).Validate().IterationCount.Should().Be(200);
        FaultInjectionOptions.Parse(["--scenario", "commit-kill"]).Validate().IterationCount.Should().Be(100);

        // 200 is the kill-loop default; passing it to another suite must not be mistaken for "unset"
        var explicitCount = FaultInjectionOptions.Parse(["--scenario", "commit-kill", "--iterations", "200"]).Validate();
        explicitCount.IterationCount.Should().Be(200);
        FaultInjectionOptions.Parse(["--size", "1024", "--rate", "2048"]).Validate().DownloadSize.Should().Be(1024);
    }

    [Fact]
    public void Defaults_are_the_nightly_kill_loop()
    {
        var options = FaultInjectionOptions.Parse([]).Validate();

        options.Scenario.Should().Be("kill");
        options.Connections.Should().Be(16);
        options.DownloadSize.Should().Be(4L * 1024 * 1024 * 1024);
        options.BytesPerSecond.Should().Be(16L * 1024 * 1024);
        options.MaxKillSeconds.Should().Be(30);
    }

    [Fact]
    public void Invalid_values_are_rejected_before_any_process_starts()
    {
        var unknown = () => FaultInjectionOptions.Parse(["--scenario", "gopher"]).Validate();
        var missing = () => FaultInjectionOptions.Parse(["--seed"]);
        var notANumber = () => FaultInjectionOptions.Parse(["--iterations", "many"]);
        var zero = () => FaultInjectionOptions.Parse(["--connections", "0"]).Validate();

        unknown.Should().Throw<ArgumentException>();
        missing.Should().Throw<ArgumentException>();
        notANumber.Should().Throw<ArgumentException>();
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }
}
