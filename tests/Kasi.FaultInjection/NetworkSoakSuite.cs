using System.Diagnostics;
using Kasi.Core;
using Kasi.FaultInjection.Toxiproxy;
using Kasi.TestServer;

namespace Kasi.FaultInjection;

/// <summary>
/// The network chaos soak (KASI-RES-004, TC-RES-004). Every job reaches the test server through its own Toxiproxy proxy and
/// meets a seeded schedule of trouble: latency with jitter for the whole run, a window throttled to 64 KiB/s, a connection reset
/// and a 30 s partition. Jobs start spread over the run's duration, a few at a time. The child process that ends without a
/// finished job is started again, as a user would resume it. Pass: every job finishes with the right SHA-256 and none sits in
/// <c>Downloading</c> without progress for two minutes.
/// </summary>
internal static class NetworkSoakSuite
{
    private const int MaxRelaunches = 5;

    private static readonly TimeSpan _stuckLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _slowFor = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _partitionFor = TimeSpan.FromSeconds(30);

    /// <summary>What happens to one job and when; drawn up front so the schedule depends on the seed only.</summary>
    internal sealed record JobPlan(int Index, TimeSpan StartAfter, int LatencyMilliseconds, TimeSpan SlowAfter, TimeSpan ResetAfter, TimeSpan PartitionAfter);

    internal static List<JobPlan> Plan(int seed, int jobs, TimeSpan window)
    {
        var random = new Random(seed);
        var slot = window / jobs;
        var plans = new List<JobPlan>(jobs);
        for (var i = 0; i < jobs; i++)
        {
            plans.Add(new JobPlan(
                i,
                (slot * i) + (slot * random.NextDouble() * 0.5),
                random.Next(100, 501),
                TimeSpan.FromSeconds(random.Next(2, 11)),
                TimeSpan.FromSeconds(random.Next(2, 11)),
                TimeSpan.FromSeconds(random.Next(2, 11))));
        }

        return plans;
    }

    public static async Task<FaultInjectionReport> RunAsync(FaultHarness harness, FaultInjectionOptions options, CancellationToken cancellationToken)
    {
        var endpoint = options.Toxiproxy ?? Environment.GetEnvironmentVariable("KASI_TOXIPROXY");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return FaultInjectionReport.NotRun(options.Scenario, options.Seed, "no Toxiproxy daemon: pass --toxiproxy <host:port> or set KASI_TOXIPROXY");
        }

        var started = Stopwatch.StartNew();
        using var http = new HttpClient { BaseAddress = new Uri($"http://{endpoint}/") };
        var client = new ToxiproxyClient(http);
        try
        {
            await client.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new FaultInjectionReport(options.Scenario, options.Seed, 0, 0, 0, 1, 0, 0, false, null, started.Elapsed, $"Toxiproxy is not reachable at {endpoint}: {ex.Message}", []);
        }

        var plans = Plan(options.Seed, options.IterationCount, TimeSpan.FromSeconds(options.DurationSeconds));
        using var gate = new SemaphoreSlim(options.Parallel);
        var results = await Task.WhenAll(plans.Select(plan => RunJobAsync(harness, options, client, plan, gate, cancellationToken))).ConfigureAwait(false);

        var failed = results.Count(r => !r.Success);
        return new FaultInjectionReport(
            options.Scenario,
            options.Seed,
            results.Length,
            results.Sum(r => r.Kills),
            results.Length - failed,
            failed,
            harness.Host.Metrics.BytesServed,
            0,
            failed == 0 && results.Length == options.IterationCount,
            null,
            started.Elapsed,
            results.FirstOrDefault(r => !r.Success)?.Error,
            results);
    }

    private static async Task<FaultIterationResult> RunJobAsync(
        FaultHarness harness,
        FaultInjectionOptions options,
        ToxiproxyClient client,
        JobPlan plan,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await Task.Delay(plan.StartAfter, cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var proxy = $"kasi-soak-{options.Seed}-{plan.Index}";
        var relaunches = 0;
        try
        {
            var listen = await client.CreateProxyAsync(proxy, "127.0.0.1:0", $"127.0.0.1:{harness.Host.BaseAddress.Port}", cancellationToken).ConfigureAwait(false);
            using var disturbance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var trouble = DisturbAsync(client, proxy, plan, disturbance.Token);
            try
            {
                var seed = $"fault-soak-{options.Seed}-{plan.Index}";
                var job = harness.NewJob($"/files/{seed}/{options.DownloadSize}", $"{seed}.bin", options.ConnectionCount, server: new Uri($"http://{listen}/"));
                await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);

                string? problem;
                do
                {
                    using var child = harness.StartChild(job.Id, options.BytesPerSecond);
                    problem = await WatchAsync(harness, child, job.Id, cancellationToken).ConfigureAwait(false);
                    FaultHarness.KillIfRunning(child);
                }
                while (problem is null && !await IsCompletedAsync(harness, job.Id, cancellationToken).ConfigureAwait(false) && ++relaunches <= MaxRelaunches);

                var path = Path.Combine(job.Folder, job.FileName);
                if (problem is null)
                {
                    problem = !await IsCompletedAsync(harness, job.Id, cancellationToken).ConfigureAwait(false)
                        ? $"the job did not finish after {MaxRelaunches} restarts"
                        : await FaultHarness.Sha256Async(path, cancellationToken).ConfigureAwait(false) == GeneratedContent.Sha256Hex(seed, options.DownloadSize)
                            ? null
                            : "the finished file does not match the generated content";
                }

                File.Delete(path);
                return new FaultIterationResult(plan.Index, problem is null, problem, 0, 0, relaunches);
            }
            finally
            {
                await disturbance.CancelAsync().ConfigureAwait(false);
                await trouble.ConfigureAwait(false);
                await client.DeleteProxyAsync(proxy, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Waits for the child to end; kills it and says why when its job sits in Downloading with no progress for too long.</summary>
    private static async Task<string?> WatchAsync(FaultHarness harness, System.Diagnostics.Process child, JobId jobId, CancellationToken cancellationToken)
    {
        var sinceProgress = Stopwatch.StartNew();
        long lastDone = -1;
        while (!child.HasExited)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var job = await harness.Jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null || job.State != JobState.Downloading || job.DoneBytes != lastDone)
            {
                lastDone = job?.DoneBytes ?? -1;
                sinceProgress.Restart();
            }
            else if (sinceProgress.Elapsed > _stuckLimit)
            {
                FaultHarness.KillIfRunning(child);
                return $"the job sat in Downloading at {lastDone} B for more than {_stuckLimit.TotalMinutes:0} minutes";
            }
        }

        return null;
    }

    private static async Task<bool> IsCompletedAsync(FaultHarness harness, JobId jobId, CancellationToken cancellationToken) =>
        (await harness.Jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false))?.State == JobState.Completed;

    /// <summary>A throttled window, a reset and a partition, one after the other, until the job is over.</summary>
    private static async Task DisturbAsync(ToxiproxyClient client, string proxy, JobPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await client.AddToxicAsync(proxy, Toxic.Latency("latency", plan.LatencyMilliseconds, plan.LatencyMilliseconds / 4), cancellationToken).ConfigureAwait(false);

            await Task.Delay(plan.SlowAfter, cancellationToken).ConfigureAwait(false);
            await client.AddToxicAsync(proxy, Toxic.Bandwidth("slow", 64), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_slowFor, cancellationToken).ConfigureAwait(false);
            await client.RemoveToxicAsync(proxy, "slow", cancellationToken).ConfigureAwait(false);

            await Task.Delay(plan.ResetAfter, cancellationToken).ConfigureAwait(false);
            await client.AddToxicAsync(proxy, Toxic.ResetPeer("reset", 0), cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await client.RemoveToxicAsync(proxy, "reset", cancellationToken).ConfigureAwait(false);

            await Task.Delay(plan.PartitionAfter, cancellationToken).ConfigureAwait(false);
            await client.AddToxicAsync(proxy, Toxic.Timeout("partition", 0), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_partitionFor, cancellationToken).ConfigureAwait(false);
            await client.RemoveToxicAsync(proxy, "partition", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the job ended before the schedule did
        }
    }
}
