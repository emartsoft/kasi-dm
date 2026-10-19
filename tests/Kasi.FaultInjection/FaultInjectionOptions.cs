using System.Globalization;

namespace Kasi.FaultInjection;

/// <summary>
/// Parsed command line for the fault harness. Iterations, size and rate stay null so the suite's own default
/// applies only when the caller passed nothing, and a value the caller passed is never replaced.
/// </summary>
public sealed record FaultInjectionOptions
{
    public const long DefaultSize = 4L * 1024 * 1024 * 1024;
    public const long DefaultRate = 16L * 1024 * 1024;

    public string Scenario { get; init; } = "kill";

    public int? Iterations { get; init; }

    public int Seed { get; init; } = Random.Shared.Next(1, int.MaxValue);

    public long? Size { get; init; }

    public int Connections { get; init; } = 16;

    public long? Rate { get; init; }

    public int MaxKillSeconds { get; init; } = 30;

    public string ReportPath { get; init; } = "fault-report.json";

    public bool Help { get; init; }

    /// <summary>Iterations once the suite default is applied: 200 kills for the kill loop, 100 for the commit kill.</summary>
    public int IterationCount => Iterations ?? (Scenario == "commit-kill" ? 100 : 200);

    public long DownloadSize => Size ?? DefaultSize;

    public long BytesPerSecond => Rate ?? DefaultRate;

    /// <summary>The command line that repeats this run's schedule: the seed and everything that changes kill times or download sizes.</summary>
    public string ToCommandLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"--scenario {Scenario} --seed {Seed} --iterations {IterationCount} --size {DownloadSize} --rate {BytesPerSecond} --connections {Connections} --max-kill-seconds {MaxKillSeconds}");

    public static FaultInjectionOptions Parse(string[] args)
    {
        var options = new FaultInjectionOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h")
            {
                options = options with { Help = true };
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{name}' needs a value.");
            }

            var value = args[++i];
            options = name switch
            {
                "--scenario" => options with { Scenario = value },
                "--iterations" => options with { Iterations = Int(value) },
                "--seed" => options with { Seed = Int(value) },
                "--size" => options with { Size = Long(value) },
                "--connections" => options with { Connections = Int(value) },
                "--rate" => options with { Rate = Long(value) },
                "--max-kill-seconds" => options with { MaxKillSeconds = Int(value) },
                "--report" => options with { ReportPath = value },
                _ => throw new ArgumentException($"Unknown option '{name}'."),
            };
        }

        return options;
    }

    public FaultInjectionOptions Validate()
    {
        if (Scenario is not ("kill" or "commit-kill"))
        {
            throw new ArgumentException($"Unknown scenario '{Scenario}'.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(IterationCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Seed, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(DownloadSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Connections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BytesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxKillSeconds, 1);
        if (string.IsNullOrWhiteSpace(ReportPath))
        {
            throw new ArgumentException("A report path is required.");
        }

        return this;
    }

    public static string Usage => """
        Kasi fault-injection harness

          --scenario <kill|commit-kill>
          --iterations <n>          kills to perform; default 200 (kill) or 100 (commit-kill)
          --seed <n>                default random; printed and stored in the report
          --size <bytes>            size of each download (default 4 GiB)
          --connections <n>         scheduler connections (default 16)
          --rate <bytes/s>          bandwidth cap for the child (default 16 MiB/s)
          --max-kill-seconds <n>    upper bound for a seeded kill delay (default 30)
          --report <path>           JSON report; summary.md is written beside it
          --help

        Child mode (used by the harness itself):
          <database> <job id> <bytes/s>
        """;

    private static int Int(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not an integer.");

    private static long Long(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not an integer.");
}
