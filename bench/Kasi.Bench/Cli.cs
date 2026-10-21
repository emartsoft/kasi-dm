using System.Globalization;
using Kasi.Bench.Macro;
using Kasi.Bench.Model;

namespace Kasi.Bench;

/// <summary>The macro modes of <c>Kasi.Bench</c>; anything else goes to BenchmarkDotNet.</summary>
public static class Cli
{
    public static readonly string[] Modes = ["throughput", "local-1gbit", "resources", "compare", "fetch", "scorecard"];

    public const string Usage = """
        Kasi.Bench macro modes (anything else runs the BenchmarkDotNet micro-benchmarks)

          throughput  --sources bench/sources.json [--connections 8,16] [--runs 5] [--local] [--out file.json] [--line-mbps n] [--window business|night]
          local-1gbit [--size 4294967296] [--connections 16] [--runs 3] [--out file.json]
          resources   --scenario baseline|idle|jobs20|load100 [--idle-seconds 600]
          compare     <previous.json> <current.json>
          fetch       --url <url> --folder <dir> --name <file> [--connections 8]   (one timed download; Compare-Idm.ps1 uses it)
          scorecard   <idm-comparison.json> [--out scorecard.md] [--release]   (median Kasi/IDM ratio against the gate)
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var mode = args[0];
        var options = Parse(args.Skip(1).ToArray());
        try
        {
            switch (mode)
            {
                case "throughput":
                    return await ThroughputRunner.RunAsync(
                        options.Required("sources"),
                        Ints(options.GetValueOrDefault("connections", "8,16")),
                        int.Parse(options.GetValueOrDefault("runs", "5"), CultureInfo.InvariantCulture),
                        options.ContainsKey("local"),
                        options.GetValueOrDefault("out"),
                        options.TryGetValue("line-mbps", out var line) ? double.Parse(line, CultureInfo.InvariantCulture) : null,
                        options.GetValueOrDefault("window", "unspecified"),
                        cancellationToken).ConfigureAwait(false);
                case "local-1gbit":
                    return await Local1GbitRunner.RunAsync(
                        long.Parse(options.GetValueOrDefault("size", (4L * 1024 * 1024 * 1024).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
                        int.Parse(options.GetValueOrDefault("connections", "16"), CultureInfo.InvariantCulture),
                        int.Parse(options.GetValueOrDefault("runs", "3"), CultureInfo.InvariantCulture),
                        options.GetValueOrDefault("out"),
                        cancellationToken).ConfigureAwait(false);
                case "resources":
                    return await ResourceRunner.RunAsync(
                        options.Required("scenario"),
                        int.Parse(options.GetValueOrDefault("idle-seconds", "600"), CultureInfo.InvariantCulture),
                        cancellationToken).ConfigureAwait(false);
                case "fetch":
                    return await FetchAsync(options, cancellationToken).ConfigureAwait(false);
                case "scorecard":
                    return await ScorecardAsync(args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).FirstOrDefault(), options, cancellationToken).ConfigureAwait(false);
                case "compare":
                    return await CompareAsync(args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray(), cancellationToken).ConfigureAwait(false);
                default:
                    throw new ArgumentException($"Unknown mode '{mode}'.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or FileNotFoundException)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> FetchAsync(Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        var timing = await EngineDownload.RunAsync(
            options.Required("url"),
            options.Required("folder"),
            options.Required("name"),
            int.Parse(options.GetValueOrDefault("connections", "8"), CultureInfo.InvariantCulture),
            null,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{(timing.Completed ? "OK" : "FAILED")} {timing.Bytes} bytes in {timing.Seconds:0.000} s"));
        return timing.Completed ? 0 : 1;
    }

    private static async Task<int> ScorecardAsync(string? file, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            throw new ArgumentException("scorecard needs a comparison session file.");
        }

        var session = await BenchmarkSession.LoadAsync(file, cancellationToken).ConfigureAwait(false);
        var result = Scorecard.Build(session, options.ContainsKey("release") ? Scorecard.ReleaseMedianLimit : Scorecard.GateMedianLimit);
        var markdown = Scorecard.ToMarkdown(result, session);
        Console.WriteLine(markdown);
        if (options.TryGetValue("out", out var path) && path.Length > 0)
        {
            await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);
        }

        return result.Passed ? 0 : 1;
    }

    private static async Task<int> CompareAsync(string[] files, CancellationToken cancellationToken)
    {
        if (files.Length != 2)
        {
            throw new ArgumentException("compare needs two session files: previous and current.");
        }

        var lines = ResultComparer.Compare(
            await BenchmarkSession.LoadAsync(files[0], cancellationToken).ConfigureAwait(false),
            await BenchmarkSession.LoadAsync(files[1], cancellationToken).ConfigureAwait(false));
        foreach (var line in lines)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{line.Source} c={line.Connections}: {line.OldMbps:0} → {line.NewMbps:0} Mbit/s ({line.ThroughputChangePercent:+0.#;-0.#}%){(line.Flagged ? "  FLAGGED: " + line.Reason : string.Empty)}"));
        }

        Console.WriteLine(lines.Count == 0 ? "nothing to compare" : lines.Any(l => l.Flagged) ? "regression flagged" : "no regression");
        return lines.Any(l => l.Flagged) ? 1 : 0;
    }

    internal static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = args[i][2..];
            options[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : string.Empty;
        }

        return options;
    }

    internal static List<int> Ints(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();

    private static string Required(this Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");
}
