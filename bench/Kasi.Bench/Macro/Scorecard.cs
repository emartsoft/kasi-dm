using System.Globalization;
using System.Text;
using Kasi.Bench.Model;

namespace Kasi.Bench.Macro;

/// <summary>One source at one connection count: median Kasi time over median IDM time (TPL 6.1.5).</summary>
public sealed record ScorecardLine(string Source, int Connections, double KasiSeconds, double IdmSeconds, double Ratio, int KasiRuns, int IdmRuns);

/// <summary>A source left out of the scorecard, and why.</summary>
public sealed record ExcludedSource(string Source, int Connections, string Reason);

/// <summary>The result of an IDM comparison session against the engine gate (median ratio at most 1.10, no source above 1.25).</summary>
public sealed record ScorecardResult(
    IReadOnlyList<ScorecardLine> Lines,
    IReadOnlyList<ExcludedSource> Excluded,
    IReadOnlyDictionary<int, double> MedianRatioByConnections,
    double WorstRatio,
    double MedianLimit,
    double WorstLimit)
{
    public bool HasData => Lines.Count > 0;

    /// <summary>The gate passes only with data, every connection count's median within the limit, and no source above the worst-case limit.</summary>
    public bool Passed => HasData && WorstRatio <= WorstLimit && MedianRatioByConnections.Values.All(m => m <= MedianLimit);
}

public static class Scorecard
{
    public const double GateMedianLimit = 1.10;
    public const double ReleaseMedianLimit = 1.05;
    public const double WorstSourceLimit = 1.25;

    /// <summary>
    /// Builds the scorecard from a session that holds rows for both tools (<c>kasi</c> and <c>idm</c>). A source and connection
    /// count is excluded, and listed, when either tool has no run that passed its check, so a failed download never counts as fast.
    /// </summary>
    public static ScorecardResult Build(BenchmarkSession session, double medianLimit = GateMedianLimit)
    {
        var lines = new List<ScorecardLine>();
        var excluded = new List<ExcludedSource>();
        foreach (var group in session.Rows.GroupBy(r => (r.Source, r.Connections)).OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => g.Key.Connections))
        {
            var kasi = group.Where(r => r.Tool.Equals("kasi", StringComparison.OrdinalIgnoreCase)).ToList();
            var idm = group.Where(r => r.Tool.Equals("idm", StringComparison.OrdinalIgnoreCase)).ToList();
            var kasiOk = kasi.Where(r => r.Sha256Ok).ToList();
            var idmOk = idm.Where(r => r.Sha256Ok).ToList();
            if (kasiOk.Count == 0 || idmOk.Count == 0)
            {
                var reason = kasi.Count == 0 || idm.Count == 0
                    ? "one tool has no runs"
                    : kasiOk.Count == 0 ? "no Kasi run produced a verified file" : "no IDM run produced a verified file";
                excluded.Add(new ExcludedSource(group.Key.Source, group.Key.Connections, reason));
                continue;
            }

            var kasiSeconds = ResultComparer.Median(kasiOk.Select(r => r.Seconds));
            var idmSeconds = ResultComparer.Median(idmOk.Select(r => r.Seconds));
            lines.Add(new ScorecardLine(group.Key.Source, group.Key.Connections, kasiSeconds, idmSeconds, kasiSeconds / idmSeconds, kasiOk.Count, idmOk.Count));
        }

        var medians = lines
            .GroupBy(l => l.Connections)
            .ToDictionary(g => g.Key, g => ResultComparer.Median(g.Select(l => l.Ratio)));
        return new ScorecardResult(lines, excluded, medians, lines.Count == 0 ? 0 : lines.Max(l => l.Ratio), medianLimit, WorstSourceLimit);
    }

    public static string ToMarkdown(ScorecardResult result, BenchmarkSession session)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Kasi vs IDM: {(result.Passed ? "PASS" : result.HasData ? "FAIL" : "NO DATA")}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Session {session.StartedUtc:u}, window {session.Window}, line speed {(session.LineSpeedMbps is { } mbps ? mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbit/s" : "not recorded")}, DNS flushed between runs: {(session.DnsFlushed ? "yes" : "no")}.");
        text.AppendLine();
        text.AppendLine("| Source | Connections | Kasi (s) | IDM (s) | Ratio | Runs (Kasi/IDM) |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var line in result.Lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {line.Source} | {line.Connections} | {line.KasiSeconds:0.0} | {line.IdmSeconds:0.0} | {line.Ratio:0.000}{(line.Ratio > result.WorstLimit ? " ✗" : string.Empty)} | {line.KasiRuns}/{line.IdmRuns} |");
        }

        text.AppendLine();
        foreach (var (connections, median) in result.MedianRatioByConnections.OrderBy(k => k.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Median ratio at {connections} connections: **{median:0.000}** (limit {result.MedianLimit:0.00}) {(median <= result.MedianLimit ? "ok" : "over")}");
        }

        if (result.HasData)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Worst source: **{result.WorstRatio:0.000}** (limit {result.WorstLimit:0.00}) {(result.WorstRatio <= result.WorstLimit ? "ok" : "over")}");
        }

        if (result.Excluded.Count > 0)
        {
            text.AppendLine().AppendLine("Excluded (not counted as fast or slow):");
            foreach (var skipped in result.Excluded)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {skipped.Source} at {skipped.Connections} connections: {skipped.Reason}");
            }
        }

        return text.ToString();
    }
}
