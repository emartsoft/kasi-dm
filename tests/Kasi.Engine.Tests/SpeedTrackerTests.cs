namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-006")]
public class SpeedTrackerTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => _t0.AddSeconds(seconds);

    [Fact]
    public void Speed_is_unknown_until_the_first_second_has_passed()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        tracker.Add(500_000, At(0.5));

        tracker.HasSample.Should().BeFalse();
        tracker.Speed(At(0.99)).Should().Be(0);
    }

    [Fact]
    public void The_first_sample_is_taken_as_it_is()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        tracker.Add(1_000_000, At(0.5));

        tracker.Speed(At(1)).Should().Be(1_000_000);
        tracker.HasSample.Should().BeTrue();
    }

    [Fact]
    public void Later_samples_are_smoothed_with_alpha_0_3()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        tracker.Add(1_000_000, At(0.9));
        _ = tracker.Speed(At(1));          // 1 MB/s
        tracker.Add(2_000_000, At(1.9));   // 2 MB in the next second

        tracker.Speed(At(2)).Should().BeApproximately((0.3 * 2_000_000) + (0.7 * 1_000_000), 1);
    }

    [Fact]
    public void A_steady_rate_stays_steady()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        for (var second = 1; second <= 10; second++)
        {
            tracker.Add(750_000, At(second - 0.01));
            tracker.Speed(At(second)).Should().BeApproximately(750_000, 1);
        }
    }

    [Fact]
    public void A_stalled_worker_decays_towards_zero_as_empty_samples_arrive()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        tracker.Add(1_000_000, At(0.9));
        _ = tracker.Speed(At(1));

        var after1 = tracker.Speed(At(2));   // one empty second
        var after3 = tracker.Speed(At(4));   // one more sample of zero over two seconds

        after1.Should().BeApproximately(700_000, 1);
        after3.Should().BeApproximately(490_000, 1);
    }

    [Fact]
    public void Time_without_a_lease_does_not_count()
    {
        var tracker = new SpeedTracker();
        tracker.Resume(At(0));
        tracker.Add(1_000_000, At(0.9));
        _ = tracker.Speed(At(1));
        tracker.Idle(At(1.5));

        tracker.Speed(At(60)).Should().BeApproximately(1_000_000, 1); // frozen while idle
        tracker.Resume(At(60));
        tracker.Add(1_000_000, At(60.4));
        tracker.Speed(At(60.5)).Should().BeApproximately(1_000_000, 1); // one second of the worker's own time has passed: 1.5 + 0.5
    }

    [Fact]
    public void Repeating_Idle_and_Resume_is_harmless_and_active_time_without_bytes_is_a_zero_sample()
    {
        var tracker = new SpeedTracker();
        tracker.Idle(At(0));     // already idle: nothing happens
        tracker.Resume(At(1));   // the window starts here
        tracker.Resume(At(2));   // already active: nothing happens
        tracker.Idle(At(3));     // two active seconds with no bytes
        tracker.Idle(At(4));     // already idle: nothing happens

        tracker.HasSample.Should().BeTrue();
        tracker.Speed(At(100)).Should().Be(0);
    }
}
