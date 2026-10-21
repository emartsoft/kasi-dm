using System.Globalization;
using Kasi.Bench.Model;
using Kasi.TestServer;

namespace Kasi.Bench.Macro;

/// <summary>
/// <c>throughput</c>: every source, every connection count (8, then 16 by default), every run, through <c>DownloadEngine</c>. This
/// is the Kasi side of the IDM comparison (TPL 6.1); the IDM side is <c>Compare-Idm.ps1</c> (M1.15b). Placeholder sources are
/// skipped with a message and never fetched.
/// </summary>
public static class ThroughputRunner
{
    public static async Task<int> RunAsync(
        string sourcesPath,
        IReadOnlyList<int> connections,
        int runs,
        bool includeLocal,
        string? outputPath,
        double? lineSpeedMbps,
        string window,
        CancellationToken cancellationToken)
    {
        var sources = await BenchmarkSourceFile.LoadAsync(sourcesPath, cancellationToken).ConfigureAwait(false);
        foreach (var skipped in sources.Sources.Where(s => s.Placeholder))
        {
            Console.WriteLine($"skip {skipped.Id}: placeholder, the owner has not supplied this source yet ({skipped.Owner})");
        }

        var folder = Path.Combine(Path.GetTempPath(), "kasi-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var server = includeLocal ? await ServerProcess.StartAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            var rows = new List<ResultRow>();
            var plan = sources.Runnable
                .Select(s => (s.Id, Url: s.Url, Size: s.SizeBytes, Seed: (string?)null))
                .Concat(server is null ? [] : sources.Local.Select(l => (l.Id, Url: new Uri(server.BaseAddress, l.Route).AbsoluteUri, Size: (long?)l.SizeBytes, Seed: (string?)SeedOf(l.Route))));
            foreach (var (id, url, size, seed) in plan)
            {
                foreach (var count in connections)
                {
                    for (var run = 1; run <= runs; run++)
                    {
                        var timing = await EngineDownload.RunAsync(url, folder, $"{id}-{count}-{run}.bin", count, null, cancellationToken).ConfigureAwait(false);
                        var ok = timing.Completed && (size is null || timing.Bytes == size)
                            && (seed is null || size is null || await Local1GbitRunner.Sha256Async(timing.FilePath, cancellationToken).ConfigureAwait(false) == GeneratedContent.Sha256Hex(seed, size.Value));
                        var mbps = EngineDownload.Megabits(timing.Bytes, timing.Seconds);
                        rows.Add(new ResultRow(id, count, run, "kasi", timing.Seconds, timing.Bytes, mbps, count, ok));
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{id} c={count} run {run}: {mbps:0} Mbit/s in {timing.Seconds:0.0} s {(ok ? "ok" : "FAILED")}"));
                        if (File.Exists(timing.FilePath))
                        {
                            File.Delete(timing.FilePath);
                        }
                    }
                }
            }

            if (outputPath is not null)
            {
                await new BenchmarkSession(
                    BenchmarkSession.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    BenchmarkSession.DescribeMachine(),
                    lineSpeedMbps,
                    window,
                    false,
                    new Dictionary<string, string> { ["kasi"] = "bench" },
                    rows).SaveAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }

            return rows.All(r => r.Sha256Ok) ? 0 : 1;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The generated-content seed in a route like <c>/files/{seed}/{size}</c>.</summary>
    internal static string? SeedOf(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? parts[1] : null;
    }
}
