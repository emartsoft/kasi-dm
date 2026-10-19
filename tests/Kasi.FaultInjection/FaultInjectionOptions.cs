using System.Globalization;

namespace Kasi.FaultInjection;

/// <summary>
/// Parsed command line for the fault harness. Iterations, size and rate stay null so the suite's own default
/// applies only when the caller passed nothing, and a value the caller passed is never replaced.
/// </summary>
public sealed record FaultInjectionOptions
{
    private const long MiB = 1024 * 1024;

    public string Scenario { get; init; } = "kill";

    public int? Iterations { get; init; }

    public int Seed { get; init; } = Random.Shared.Next(1, int.MaxValue);

    public long? Size { get; init; }

    public int? Connections { get; init; }

    public long? Rate { get; init; }

    public int MaxKillSeconds { get; init; } = 30;

    /// <summary>Size of the volume the disk-pressure suite fills (2 GiB in the test plan).</summary>
    public int VhdMebibytes { get; init; } = 2048;

    /// <summary>How long a paused download waits before the disk-pressure suite frees space (TPL 7.1: 30 s).</summary>
    public int FreeAfterSeconds { get; init; } = 30;

    /// <summary>Toxiproxy's REST address (<c>host:port</c>) for the network soak; falls back to the KASI_TOXIPROXY environment variable.</summary>
    public string? Toxiproxy { get; init; }

    /// <summary>The window the soak spreads its jobs over (TPL 7.1: up to 2 h).</summary>
    public int DurationSeconds { get; init; } = 7200;

    /// <summary>How many soak jobs may run at the same time.</summary>
    public int Parallel { get; init; } = 10;

    public string ReportPath { get; init; } = "fault-report.json";

    public bool Help { get; init; }

    /// <summary>The count of the test plan for the suite (TPL 7.1): kills, mutations, or pressure and stall rounds.</summary>
    public int IterationCount => Iterations ?? Scenario switch
    {
        "commit-kill" => 100,
        "server-mutation" => 50,
        "disk-pressure" => 20,
        "storage-stall" => 10,
        "network-soak" => 50,
        _ => 200,
    };

    public long DownloadSize => Size ?? Scenario switch
    {
        "server-mutation" => 24 * MiB,
        "storage-stall" => 32 * MiB,
        "disk-pressure" => 512 * MiB,
        "network-soak" => 64 * MiB,
        _ => 4096 * MiB,
    };

    public long BytesPerSecond => Rate ?? Scenario switch
    {
        "server-mutation" => 4 * MiB,
        "storage-stall" => 8 * MiB,
        "disk-pressure" => 64 * MiB,
        "network-soak" => 4 * MiB,
        _ => 16 * MiB,
    };

    /// <summary>One connection for the stall suite so a guard that fires shows as a single aborted request.</summary>
    public int ConnectionCount => Connections ?? Scenario switch
    {
        "server-mutation" => 4,
        "storage-stall" => 1,
        "disk-pressure" => 4,
        "network-soak" => 4,
        _ => 16,
    };

    /// <summary>The command line that repeats this run's schedule: the seed and everything that changes kill times or download sizes.</summary>
    public string ToCommandLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"--scenario {Scenario} --seed {Seed} --iterations {IterationCount} --size {DownloadSize} --rate {BytesPerSecond} --connections {ConnectionCount} --max-kill-seconds {MaxKillSeconds}");

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
                "--vhd-mib" => options with { VhdMebibytes = Int(value) },
                "--free-after-seconds" => options with { FreeAfterSeconds = Int(value) },
                "--toxiproxy" => options with { Toxiproxy = value },
                "--duration-seconds" => options with { DurationSeconds = Int(value) },
                "--parallel" => options with { Parallel = Int(value) },
                "--report" => options with { ReportPath = value },
                _ => throw new ArgumentException($"Unknown option '{name}'."),
            };
        }

        return options;
    }

    public FaultInjectionOptions Validate()
    {
        if (Scenario is not ("kill" or "commit-kill" or "server-mutation" or "storage-stall" or "disk-pressure" or "network-soak"))
        {
            throw new ArgumentException($"Unknown scenario '{Scenario}'.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(IterationCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Seed, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(DownloadSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConnectionCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(VhdMebibytes, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(FreeAfterSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(DurationSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Parallel, 1);
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

          --scenario <kill|commit-kill|server-mutation|storage-stall|disk-pressure|network-soak>
          --iterations <n>          kills, mutated jobs or rounds; default 200, 100, 50, 10, 20 and 50 for the suites in that order
          --seed <n>                default random; printed and stored in the report
          --size <bytes>            size of each download (default depends on the suite; 4 GiB for the kill suites)
          --connections <n>         scheduler connections (default depends on the suite; 16 for the kill suites)
          --rate <bytes/s>          bandwidth cap for the child (default depends on the suite; 16 MiB/s for the kill suites)
          --max-kill-seconds <n>    upper bound for a seeded kill delay (default 30)
          --vhd-mib <n>             disk-pressure volume size (default 2048; needs Administrator)
          --free-after-seconds <n>  disk-pressure wait before space is freed (default 30)
          --toxiproxy <host:port>   Toxiproxy REST address for network-soak (or KASI_TOXIPROXY)
          --duration-seconds <n>    network-soak window the jobs are spread over (default 7200)
          --parallel <n>            network-soak jobs at the same time (default 10)
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
