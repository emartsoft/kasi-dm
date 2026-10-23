namespace Kasi.Scheduling;

/// <summary>
/// Fractional indexing helper for ordering items in queues (TRD 11.2, KASI-QUE-002).
/// Assigns intermediate float positions between adjacent items without whole-table renumbering.
/// </summary>
public static class FractionalIndex
{
    public const double DefaultStep = 1000.0;
    public const double MinGap = 1e-6;

    /// <summary>Calculates a position between two existing items.</summary>
    public static double Between(double before, double after) => (before + after) / 2.0;

    /// <summary>Calculates a position before the first item in a queue.</summary>
    public static double Before(double first) => first > DefaultStep ? first - DefaultStep : first / 2.0;

    /// <summary>Calculates a position after the last item in a queue.</summary>
    public static double After(double last) => last + DefaultStep;

    /// <summary>Returns true if the gap between two positions is too narrow, requiring a rebalance.</summary>
    public static bool NeedsRebalance(double before, double after) => Math.Abs(after - before) < MinGap;
}
