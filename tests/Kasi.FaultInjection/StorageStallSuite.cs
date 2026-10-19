using System.Diagnostics;
using Kasi.TestServer;

namespace Kasi.FaultInjection;

/// <summary>
/// The storage-stall suite (TPL 7.1, TC-ENG-015 resilience). A slow disk must not be mistaken for a dead network: the child
/// delays some part-file writes by 5 s while the read-stall timeout is 1 s. The stall guard is armed only around the network
/// read (M1.11), so nothing may be aborted or fetched again. Pass: the download completes with the right SHA-256, the server saw
/// no aborted request and sent no byte twice.
/// </summary>
internal static class StorageStallSuite
{
    private const int StalledWrites = 2;

    private static readonly TimeSpan _stall = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _readStall = TimeSpan.FromSeconds(1);

    public static async Task<FaultInjectionReport> RunAsync(FaultHarness harness, FaultInjectionOptions options, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var results = new List<FaultIterationResult>();
        var verified = 0;
        string? error = null;
        string? lastSha = null;

        for (var i = 0; i < options.IterationCount && error is null; i++)
        {
            var seed = $"fault-stall-{options.Seed}-{i}";
            var job = harness.NewJob($"/files/{seed}/{options.DownloadSize}", $"{seed}.bin", options.ConnectionCount);
            await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            var servedBefore = harness.Host.Metrics.BytesServed;
            var abortedBefore = harness.Host.Metrics.AbortedRequests;

            string? problem = null;
            using (var child = harness.StartChild(job.Id, options.BytesPerSecond, ChildEnvironment.StorageStall(_stall, StalledWrites, _readStall)))
            {
                var limit = TimeSpan.FromSeconds((options.DownloadSize / options.BytesPerSecond * 3) + 120);
                if (!await FaultHarness.WaitOrTimeoutAsync(child, limit, cancellationToken).ConfigureAwait(false) || child.ExitCode != 0)
                {
                    var exit = child.HasExited ? child.ExitCode : -1;
                    FaultHarness.KillIfRunning(child);
                    problem = $"the child did not complete (exit {exit})";
                }
            }

            var served = harness.Host.Metrics.BytesServed - servedBefore;
            var refetched = Math.Max(0, served - options.DownloadSize);
            var path = Path.Combine(job.Folder, job.FileName);
            if (problem is null)
            {
                lastSha = await FaultHarness.Sha256Async(path, cancellationToken).ConfigureAwait(false);
                var aborted = harness.Host.Metrics.AbortedRequests - abortedBefore;
                if (lastSha != GeneratedContent.Sha256Hex(seed, options.DownloadSize))
                {
                    problem = "the finished file does not match the generated content";
                }
                else if (aborted > 0 || refetched > 0)
                {
                    problem = $"a storage stall was treated as a network failure ({aborted} aborted request(s), {refetched} B fetched twice)";
                }
            }

            if (problem is null)
            {
                verified++;
            }

            results.Add(new FaultIterationResult(i, problem is null, problem, served, refetched, 0));
            error ??= problem;
            File.Delete(path);
        }

        return new FaultInjectionReport(
            options.Scenario,
            options.Seed,
            results.Count,
            0,
            verified,
            results.Count(r => !r.Success),
            harness.Host.Metrics.BytesServed,
            results.Count == 0 ? 0 : results.Max(r => r.RefetchedBytes),
            error is null && verified == options.IterationCount,
            lastSha,
            started.Elapsed,
            error,
            results);
    }
}
