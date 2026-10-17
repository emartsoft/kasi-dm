namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-004")]
[Trait("Requirement", "KASI-ENG-005")]
[Trait("Requirement", "KASI-ENG-006")]
public class SegmentSchedulerOptionsTests
{
    [Fact]
    public void Defaults_are_the_values_of_TRD_6_2_and_6_3()
    {
        var options = new SegmentSchedulerOptions();

        options.MaxConnections.Should().Be(8);
        options.MinSplitSize.Should().Be(1024 * 1024);
        options.PageAlignment.Should().Be(64 * 1024);
        options.MinLeaseBytes.Should().Be(512 * 1024);
        options.MaxLeaseBytes.Should().Be(16 * 1024 * 1024);
        options.LeaseTargetDuration.Should().Be(TimeSpan.FromSeconds(2));
        options.SlowLaneSpeedRatio.Should().Be(0.30);
        options.SlowLaneSustainedDuration.Should().Be(TimeSpan.FromSeconds(10));
        options.SlowLaneMinRemainingMultiplier.Should().Be(4);
        options.SlowLaneMinActiveWorkers.Should().Be(3);
        options.MaxEndgameDuplicateBytes.Should().Be(2 * 1024 * 1024);
        options.RampUpInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        options.RampUpThroughputGainThreshold.Should().Be(0.05);
        options.ShrinkTailLeases.Should().BeFalse();
        options.Validate();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void The_connection_limits_of_the_TRD_are_accepted(int connections)
    {
        var act = () => (new SegmentSchedulerOptions { MaxConnections = connections }).Validate();

        act.Should().NotThrow();
    }

    public static TheoryData<string, SegmentSchedulerOptions> Invalid => new()
    {
        { "0 connections", new SegmentSchedulerOptions { MaxConnections = 0 } },
        { "33 connections", new SegmentSchedulerOptions { MaxConnections = 33 } },
        { "zero min split", new SegmentSchedulerOptions { MinSplitSize = 0 } },
        { "zero alignment", new SegmentSchedulerOptions { PageAlignment = 0 } },
        { "zero min lease", new SegmentSchedulerOptions { MinLeaseBytes = 0 } },
        { "max lease below min lease", new SegmentSchedulerOptions { MinLeaseBytes = 1000, MaxLeaseBytes = 999 } },
        { "zero lease duration", new SegmentSchedulerOptions { LeaseTargetDuration = TimeSpan.Zero } },
        { "slow ratio 0", new SegmentSchedulerOptions { SlowLaneSpeedRatio = 0 } },
        { "slow ratio 1", new SegmentSchedulerOptions { SlowLaneSpeedRatio = 1 } },
        { "zero slow duration", new SegmentSchedulerOptions { SlowLaneSustainedDuration = TimeSpan.Zero } },
        { "slow multiplier 0", new SegmentSchedulerOptions { SlowLaneMinRemainingMultiplier = 0 } },
        { "one slow-lane worker", new SegmentSchedulerOptions { SlowLaneMinActiveWorkers = 1 } },
        { "negative endgame cap", new SegmentSchedulerOptions { MaxEndgameDuplicateBytes = -1 } },
        { "zero ramp-up interval", new SegmentSchedulerOptions { RampUpInterval = TimeSpan.Zero } },
        { "negative ramp-up gain", new SegmentSchedulerOptions { RampUpThroughputGainThreshold = -0.1 } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_value_the_scheduler_cannot_work_with_is_rejected(string reason, SegmentSchedulerOptions options)
    {
        var act = options.Validate;

        act.Should().Throw<ArgumentOutOfRangeException>(reason);
    }

    [Theory]
    [InlineData(0.0, 512 * 1024L)]                      // cold worker
    [InlineData(-5.0, 512 * 1024L)]
    [InlineData(double.NaN, 512 * 1024L)]
    [InlineData(double.PositiveInfinity, 512 * 1024L)]
    [InlineData(100_000.0, 512 * 1024L)]                // 0.2 MB: below the minimum
    [InlineData(1024.0 * 1024, 2 * 1024 * 1024L)]       // 1 MiB/s: two seconds
    [InlineData(5.0 * 1024 * 1024, 10 * 1024 * 1024L)]
    [InlineData(8.0 * 1024 * 1024, 16 * 1024 * 1024L)]  // exactly the cap
    [InlineData(20.0 * 1024 * 1024, 16 * 1024 * 1024L)] // clamped
    public void Lease_size_is_two_seconds_of_speed_clamped_to_the_limits(double bytesPerSecond, long expected)
    {
        new SegmentSchedulerOptions().LeaseSizeFor(bytesPerSecond).Should().Be(expected);
    }

    [Fact]
    public void A_lease_knows_its_length()
    {
        new LeaseAssignment(LeaseId: 1, WorkerId: 0, SegmentIndex: 0, Start: 100, End: 612).Length.Should().Be(512);
    }
}
