using System.Globalization;
using Kasi.Bench.Model;

namespace Kasi.Bench.Macro;

/// <summary>One source and connection count compared between two sessions.</summary>
public sealed record ComparisonLine(string Source, int Connections, double OldMbps, double NewMbps, double ThroughputChangePercent, double? MemoryChangePercent, bool Flagged, string? Reason);

/// <summary>
/// Regression tracking (TPL 6.4): the median throughput per source and connection count is compared with the previous session. A
/// throughput drop of more than 5% or a memory increase of more than 10% is flagged.
/// </summary>
public static class ResultComparer
{
    public const double MaxThroughputDropPercent = 5;
    public const double MaxMemoryIncreasePercent = 10;

    public static IReadOnlyList<ComparisonLine> Compare(BenchmarkSession previous, BenchmarkSession current, string tool = "kasi")
    {
        var lines = new List<ComparisonLine>();
        var old = Medians(previous, tool);
        foreach (var (key, now) in Medians(current, tool).OrderBy(k => k.Key.Source, StringComparer.Ordinal).ThenBy(k => k.Key.Connections))
        {
            if (!old.TryGetValue(key, out var before) || before.Mbps <= 0)
            {
                continue;
            }

            var throughput = (now.Mbps - before.Mbps) / before.Mbps * 100;
            double? memory = before.Memory is > 0 && now.Memory is not null ? (now.Memory.Value - before.Memory.Value) / before.Memory.Value * 100 : null;
            string? reason = null;
            if (throughput < -MaxThroughputDropPercent)
            {
                reason = string.Create(CultureInfo.InvariantCulture, $"throughput fell {-throughput:0.#}% (limit {MaxThroughputDropPercent}%)");
            }
            else if (memory > MaxMemoryIncreasePercent)
            {
                reason = string.Create(CultureInfo.InvariantCulture, $"memory rose {memory:0.#}% (limit {MaxMemoryIncreasePercent}%)");
            }

            lines.Add(new ComparisonLine(key.Source, key.Connections, before.Mbps, now.Mbps, throughput, memory, reason is not null, reason));
        }

        return lines;
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidOperationException("No values.");
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static Dictionary<(string Source, int Connections), (double Mbps, double? Memory)> Medians(BenchmarkSession session, string tool) =>
        session.Rows
            .Where(r => r.Sha256Ok && string.Equals(r.Tool, tool, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => (r.Source, r.Connections))
            .ToDictionary(
                g => g.Key,
                g => (Median(g.Select(r => r.Mbps)), g.Any(r => r.PeakWorkingSetMegabytes is not null) ? Median(g.Where(r => r.PeakWorkingSetMegabytes is not null).Select(r => r.PeakWorkingSetMegabytes!.Value)) : (double?)null));
}
