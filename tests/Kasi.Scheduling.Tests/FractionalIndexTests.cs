using Kasi.Scheduling;

namespace Kasi.Scheduling.Tests;

public sealed class FractionalIndexTests
{
    [Fact]
    public void Between_returns_exact_midpoint()
    {
        FractionalIndex.Between(1000.0, 2000.0).Should().Be(1500.0);
        FractionalIndex.Between(1500.0, 2000.0).Should().Be(1750.0);
    }

    [Fact]
    public void Before_returns_subtracted_or_halved_position()
    {
        FractionalIndex.Before(3000.0).Should().Be(2000.0);
        FractionalIndex.Before(1000.0).Should().Be(500.0);
        FractionalIndex.Before(500.0).Should().Be(250.0);
    }

    [Fact]
    public void After_returns_incremented_position()
    {
        FractionalIndex.After(1000.0).Should().Be(2000.0);
        FractionalIndex.After(3500.0).Should().Be(4500.0);
    }

    [Fact]
    public void NeedsRebalance_detects_narrow_gaps()
    {
        FractionalIndex.NeedsRebalance(1.0, 1.00000001).Should().BeTrue();
        FractionalIndex.NeedsRebalance(1.0, 1.01).Should().BeFalse();
    }
}
