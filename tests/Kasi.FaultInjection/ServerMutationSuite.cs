using System.Diagnostics;
using Kasi.Core;
using Kasi.TestServer;

namespace Kasi.FaultInjection;

/// <summary>
/// The server-mutation suite (KASI-RES-005, KASI-RES-006, TC-RES-005, TC-RES-006). Each iteration is its own job on
/// <c>/mutable/{id}</c> and the content changes once or twice, at a seeded moment: between two sessions (the child is stopped,
/// the file changes, a new child resumes) or in the middle of a session. The engine restarts a changed file at most twice, so
/// two changes is the most one job can take. Pass: a change between sessions is always detected, and the finished file is exactly
/// one complete version (the one the job last restarted on), never a mix.
/// </summary>
internal static class ServerMutationSuite
{
    public static async Task<FaultInjectionReport> RunAsync(FaultHarness harness, FaultInjectionOptions options, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var random = new Random(options.Seed);
        var results = new List<FaultIterationResult>();
        var verified = 0;
        var stops = 0;
        string? error = null;

        for (var i = 0; i < options.IterationCount && error is null; i++)
        {
            var id = $"fault-mutation-{options.Seed}-{i}";
            harness.Host.Mutable.Change(id, options.DownloadSize); // version 1
            var job = harness.NewJob($"/mutable/{id}", $"{id}.bin", options.ConnectionCount);
            await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            var servedBefore = harness.Host.Metrics.BytesServed;

            // 0: between sessions, 1: in the middle of a session, 2: both (two changes, the engine's cap)
            var shape = random.Next(3);
            var problem = await RunOneAsync(harness, options, job, id, shape, cancellationToken).ConfigureAwait(false);
            if (shape != 1)
            {
                stops++;
            }

            if (problem is null)
            {
                verified++;
            }

            results.Add(new FaultIterationResult(i, problem is null, problem, harness.Host.Metrics.BytesServed - servedBefore, 0, shape != 1 ? 1 : 0));
            error ??= problem;
            File.Delete(Path.Combine(job.Folder, job.FileName));
        }

        return new FaultInjectionReport(
            options.Scenario,
            options.Seed,
            results.Count,
            stops,
            verified,
            results.Count(r => !r.Success),
            harness.Host.Metrics.BytesServed,
            0,
            error is null && verified == options.IterationCount,
            null,
            started.Elapsed,
            error,
            results);
    }

    private static async Task<string?> RunOneAsync(
        FaultHarness harness,
        FaultInjectionOptions options,
        Job job,
        string id,
        int shape,
        CancellationToken cancellationToken)
    {
        var version = 1;
        if (shape is 0 or 2)
        {
            using var first = harness.StartChild(job.Id, options.BytesPerSecond);
            if (!await harness.WaitForProgressAsync(first, job.Id, options.DownloadSize, 0.7, 0, cancellationToken).ConfigureAwait(false))
            {
                FaultHarness.KillIfRunning(first);
                return "the child ended or checkpointed nothing before the file could be changed (" + harness.LastProgressNote + ")";
            }

            FaultHarness.KillIfRunning(first);
            await first.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            harness.Host.Mutable.Change(id);
            version++;
        }

        using var second = harness.StartChild(job.Id, options.BytesPerSecond);
        if (shape is 1 or 2)
        {
            // after a change between sessions the job has to restart first, or both changes would look like one
            if (!await harness.WaitForProgressAsync(second, job.Id, options.DownloadSize, 0.7, version - 1, cancellationToken).ConfigureAwait(false))
            {
                FaultHarness.KillIfRunning(second);
                return "the child ended or checkpointed nothing before the file could be changed (" + harness.LastProgressNote + ")";
            }

            harness.Host.Mutable.Change(id);
            version++;
        }

        var limit = TimeSpan.FromSeconds((options.DownloadSize / options.BytesPerSecond * 3) + 120);
        if (!await FaultHarness.WaitOrTimeoutAsync(second, limit, cancellationToken).ConfigureAwait(false) || second.ExitCode != 0)
        {
            var exit = second.HasExited ? second.ExitCode : -1;
            FaultHarness.KillIfRunning(second);
            return $"the child did not complete (exit {exit})";
        }

        var final = (await harness.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
        var changes = version - 1;
        if (final.State != JobState.Completed)
        {
            return $"the job ended in state {final.State}, not Completed";
        }

        // A change between sessions meets a new probe, so it is always detected. A change in the middle of a session is seen
        // only by the next request; if every lease was already open, the whole file is still one version (the old one).
        var guaranteed = shape is 0 or 2 ? 1 : 0;
        if (final.RestartCount < guaranteed || final.RestartCount > changes)
        {
            return $"{changes} change(s), {guaranteed} of them between sessions, but {final.RestartCount} restart(s)";
        }

        // never a mix: the file is exactly the version the job last restarted on
        var expected = final.RestartCount + 1;
        var sha = await FaultHarness.Sha256Async(Path.Combine(job.Folder, job.FileName), cancellationToken).ConfigureAwait(false);
        return sha == GeneratedContent.Sha256Hex($"{id}.v{expected}", options.DownloadSize)
            ? null
            : $"after {final.RestartCount} restart(s) the finished file is not version {expected} of the content (a stale or mixed file)";
    }
}
