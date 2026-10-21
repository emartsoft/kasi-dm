using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using Kasi.Bench.Model;
using Kasi.TestServer;

namespace Kasi.Bench.Macro;

public enum ThroughputVerdict
{
    Pass,
    Fail,

    /// <summary>The machine plus server cannot reach the target by themselves, so the engine cannot be judged.</summary>
    Inconclusive,
}

/// <summary>
/// The 1 Gbit/s check (KASI-NFR-002, TPL 6.3): one 16-connection download from a <c>Kasi.TestServer</c> process. A raw
/// <see cref="HttpClient"/> run with the same number of parallel range streams is the control: it is the ceiling of this machine
/// and this server, so a miss can be told apart from an engine problem.
/// </summary>
public static class Local1GbitRunner
{
    public const double TargetMbps = 1000;

    public static ThroughputVerdict Evaluate(double controlMbps, double engineMbps) =>
        controlMbps < TargetMbps ? ThroughputVerdict.Inconclusive : engineMbps >= TargetMbps ? ThroughputVerdict.Pass : ThroughputVerdict.Fail;

    public static async Task<int> RunAsync(long size, int connections, int runs, string? outputPath, CancellationToken cancellationToken)
    {
        await using var server = await ServerProcess.StartAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetTempPath(), "kasi-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var control = await RawControlMbpsAsync(server.BaseAddress, "bench-control", size, connections, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"control (raw HttpClient, {connections} range streams): {control:0} Mbit/s"));

            var rows = new List<ResultRow>();
            var allHashesMatch = true;
            for (var run = 1; run <= runs; run++)
            {
                var seed = $"bench-1gbit-{run}";
                var url = new Uri(server.BaseAddress, $"files/{seed}/{size}").AbsoluteUri;
                var timing = await EngineDownload.RunAsync(url, folder, seed + ".bin", connections, null, cancellationToken).ConfigureAwait(false);

                // hashing happens after the clock stopped: it is reported, not charged to throughput
                var hashOk = timing.Completed && await Sha256Async(timing.FilePath, cancellationToken).ConfigureAwait(false) == GeneratedContent.Sha256Hex(seed, size);
                allHashesMatch &= hashOk;
                var mbps = EngineDownload.Megabits(timing.Bytes, timing.Seconds);
                rows.Add(new ResultRow("local-1gbit", connections, run, "kasi", timing.Seconds, timing.Bytes, mbps, connections, hashOk));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"run {run}: {mbps:0} Mbit/s in {timing.Seconds:0.0} s, sha256 {(hashOk ? "ok" : "MISMATCH")}"));
                File.Delete(timing.FilePath);
            }

            var engine = ResultComparer.Median(rows.Select(r => r.Mbps));
            var verdict = Evaluate(control, engine);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"engine median {engine:0} Mbit/s ({engine / Math.Max(control, 1) * 100:0}% of the control) → {verdict}"));

            if (outputPath is not null)
            {
                var session = new BenchmarkSession(
                    BenchmarkSession.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    BenchmarkSession.DescribeMachine(),
                    null,
                    "local",
                    false,
                    new Dictionary<string, string> { ["kasi"] = "bench", ["control"] = string.Create(CultureInfo.InvariantCulture, $"{control:0} Mbit/s") },
                    rows);
                await session.SaveAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }

            return !allHashesMatch || verdict == ThroughputVerdict.Fail ? 1 : 0;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The same number of parallel range requests as the engine uses, read and thrown away.</summary>
    public static async Task<double> RawControlMbpsAsync(Uri server, string seed, long size, int streams, CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // HttpClient owns and disposes its handler
        using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = streams }) { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore CA2000
        var slice = size / streams;
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async i =>
        {
            var from = i * slice;
            var to = i == streams - 1 ? size - 1 : from + slice - 1;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, $"files/{seed}/{size}"));
            request.Headers.Range = new RangeHeaderValue(from, to);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                await body.CopyToAsync(Stream.Null, 81920, cancellationToken).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
        return EngineDownload.Megabits(size, clock.Elapsed.TotalSeconds);
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
    }
}
