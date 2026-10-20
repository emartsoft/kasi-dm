using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kasi.FaultInjection;

/// <summary>One kill (or one failed step): what the child served, how much of it was lost, and what the database said afterwards.</summary>
public sealed record FaultIterationResult(
    int Index,
    bool Success,
    string? Error,
    long BytesServed,
    long RefetchedBytes,
    int Kills);

/// <summary>The nightly report. It never contains URLs, query strings or user paths (CLAUDE.md rule 3).</summary>
public sealed record FaultInjectionReport(
    string Scenario,
    int Seed,
    int Iterations,
    int Kills,
    int Completed,
    int Failed,
    long BytesServed,
    long MaxRefetchedBytes,
    bool ChecksumVerified,
    string? FinalSha256,
    TimeSpan Duration,
    string? Error,
    IReadOnlyList<FaultIterationResult> Results,
    string? SkipReason = null,
    string? Replay = null)
{
    /// <summary>A suite that could not run here (for example no Administrator rights to mount a volume) is neither a pass nor a failure.</summary>
    public bool Skipped => SkipReason is not null;

    public bool Passed => !Skipped && Failed == 0 && Error is null && ChecksumVerified;

    public static FaultInjectionReport NotRun(string scenario, int seed, string reason) =>
        new(scenario, seed, 0, 0, 0, 0, 0, 0, false, null, TimeSpan.Zero, null, [], reason);

    /// <summary>The arguments that replay this schedule: every option that shapes it, not only the seed.</summary>
    public string ToCommandLine() =>
        Replay ?? string.Create(CultureInfo.InvariantCulture, $"--scenario {Scenario} --seed {Seed} --iterations {Math.Max(Iterations, 1)}");

    /// <summary>The summary a person reads first: the verdict, the counters and every failed iteration.</summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Fault injection: {Scenario} ({(Skipped ? "SKIPPED" : Passed ? "PASS" : "FAIL")})");
        text.AppendLine();
        text.AppendLine("| Seed | Iterations | Kills | Downloads verified | Failed | Max refetch (B) | Duration |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| {Seed} | {Iterations} | {Kills} | {Completed} | {Failed} | {MaxRefetchedBytes} | {Duration:g} |");
        if (SkipReason is not null)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Not run:** {SkipReason}");
        }

        if (Error is not null)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Error:** {Error}");
        }

        foreach (var failure in Results.Where(r => !r.Success))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- iteration {failure.Index}: {failure.Error}");
        }

        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Re-run the same schedule with `{ToCommandLine()}`.");
        return text.ToString();
    }

    /// <summary>Writes the JSON report to <paramref name="path"/> and summary.md beside it.</summary>
    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        await using (var stream = File.Create(full))
        {
            await JsonSerializer.SerializeAsync(stream, this, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), ToMarkdown(), cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
