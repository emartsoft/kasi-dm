using Avalonia;

namespace Kasi.App.Controls;

/// <summary>The points of a sparkline: the newest sample at the right edge, the oldest dropped off the left, scaled to a maximum.</summary>
internal static class SparklineGeometry
{
    /// <summary>
    /// Points for <paramref name="samples"/> in a box of this size. With <paramref name="capacity"/> samples the line fills the width; with
    /// fewer it starts further right, so a young history grows in from the right edge as in the redesign. A flat zero line sits on the floor.
    /// </summary>
    public static IReadOnlyList<Point> Points(IReadOnlyList<double> samples, double width, double height, int capacity, double? maximum = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0 || width <= 0 || height <= 0 || capacity < 2)
        {
            return [];
        }

        var shown = samples.Count > capacity ? samples.Skip(samples.Count - capacity).ToList() : [.. samples];
        var top = maximum is { } given && given > 0 ? given : Math.Max(shown.Max(), 1e-9);
        var step = width / (capacity - 1);
        var first = (capacity - shown.Count) * step;
        var points = new List<Point>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var value = Math.Clamp(shown[i], 0, top);
            points.Add(new Point(first + (i * step), height - (value / top * height)));
        }

        return points;
    }
}
