using System.Diagnostics;

namespace Kasi.Engine.Tests;

/// <summary>
/// Rate measurement for the bandwidth tests (KASI-BWL-001, KASI-BWL-002). A token bucket cannot hand out more than its rate times
/// the window plus one full bucket (a quarter second of the rate, ADR 0010) plus the one reservation in hand, so that is the
/// upper bound whatever the machine does. A loaded runner delays timers and reads, which only lowers the measured rate, so the
/// lower bound is a generous share of the cap.
/// </summary>
internal static class BandwidthMeasure
{
    /// <summary>Lowest share of the cap a steady window must reach on a loaded 4-core runner.</summary>
    public const double LoadedRunnerFloor = 0.75;

    /// <summary>Counts what <paramref name="consumed"/> grants over <paramref name="window"/>; both samples sit next to their timestamps.</summary>
    public static async Task<Window> MeasureAsync(Func<long> consumed, TimeSpan window, CancellationToken cancellationToken)
    {
        var bytesBefore = consumed();
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(window, cancellationToken);
        var bytesAfter = consumed();
        return new Window(bytesAfter - bytesBefore, Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    /// <summary>Asserts the cap is honoured (never exceeded by more than the bucket) and the download kept at least <see cref="LoadedRunnerFloor"/> of it.</summary>
    public static void AssertAtCap(Window measured, long cap, string because = "")
    {
        var ceiling = ((cap * measured.Seconds) + (cap / 4d) + BandwidthLimiter.MaximumReadBytes) / measured.Seconds;
        measured.Rate.Should().BeLessThanOrEqualTo(ceiling, "the cap is never exceeded by more than one bucket " + because);
        measured.Rate.Should().BeGreaterThanOrEqualTo(cap * LoadedRunnerFloor, because);
    }

    internal readonly record struct Window(long Bytes, double Seconds)
    {
        public double Rate => Bytes / Seconds;
    }
}
