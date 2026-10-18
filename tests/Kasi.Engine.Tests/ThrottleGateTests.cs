using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-RES-004")]
public sealed class ThrottleGateTests
{
    [Fact]
    public void Only_the_first_throttle_in_a_window_asks_for_a_connection_reduction()
    {
        var time = new FakeTimeProvider();
        var gate = new ThrottleGate(time);

        var first = gate.Enter(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(1));
        var sibling = gate.Enter(TimeSpan.FromSeconds(5));

        first.Should().BeTrue();
        sibling.Should().BeFalse("eight workers hitting 429 together must reduce the pool once, not eight times");
    }

    [Fact]
    public void The_window_ends_after_the_longest_wait_and_a_later_throttle_starts_a_new_one()
    {
        var time = new FakeTimeProvider();
        var gate = new ThrottleGate(time);
        gate.Enter(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(1));
        gate.Enter(TimeSpan.FromSeconds(5));

        gate.Remaining().Should().Be(TimeSpan.FromSeconds(5), "the window extends to the longest wait");
        time.Advance(TimeSpan.FromSeconds(5));
        gate.Remaining().Should().Be(TimeSpan.Zero);
        gate.Enter(TimeSpan.FromSeconds(1)).Should().BeTrue("a throttle after the window is a new episode");
    }
}
