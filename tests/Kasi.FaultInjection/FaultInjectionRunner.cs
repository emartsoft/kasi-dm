using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.FaultInjection;

/// <summary>
/// The kill-loop suites (KASI-RES-001, TC-RES-001 and TC-RES-011). One iteration is one kill: a child process runs a throttled
/// download through <c>DownloadEngine</c>, is killed, and the harness checks the database, the bytes it had to fetch again and,
/// when a download finishes, its SHA-256. The next child recovers and resumes; a finished download is replaced by a new one so
/// the loop keeps killing until the requested count is reached.
/// </summary>
public static class FaultInjectionRunner
{
    /// <summary>Each connection's socket buffers can hold this much that the server counted as sent but the child never read or saved.</summary>
    private const long SocketBufferBytesPerConnection = 1024 * 1024;

    private static readonly TimeSpan _commitWait = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan _schedulerJitter = TimeSpan.FromSeconds(0.5);

    public static async Task<int> RunAsync(FaultInjectionOptions requested, CancellationToken cancellationToken = default)
    {
        if (requested.Help)
        {
            await Console.Out.WriteLineAsync(FaultInjectionOptions.Usage).ConfigureAwait(false);
            return 0;
        }

        var options = requested.Validate();
        if (options.Scenario == "power-guest")
        {
            return await PowerGuest.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }

        var report = await RunToReportAsync(options, cancellationToken).ConfigureAwait(false);
        await report.WriteAsync(options.ReportPath, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"{report.Scenario}: seed={report.Seed} kills={report.Kills} downloads-verified={report.Completed} " +
            $"failed={report.Failed} max-refetch={report.MaxRefetchedBytes} duration={report.Duration:g}").ConfigureAwait(false);
        if (report.Skipped)
        {
            await Console.Out.WriteLineAsync($"SKIPPED {report.Scenario}: {report.SkipReason}").ConfigureAwait(false);
        }
        else if (!report.Passed)
        {
            await Console.Error.WriteLineAsync($"FAILED: {report.Error ?? "see the report"}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Re-run: {report.ToCommandLine()}").ConfigureAwait(false);
        }

        return report.Passed || report.Skipped ? 0 : 1;
    }

    public static async Task<FaultInjectionReport> RunToReportAsync(FaultInjectionOptions options, CancellationToken cancellationToken = default)
    {
        await using var harness = await FaultHarness.CreateAsync(cancellationToken).ConfigureAwait(false);
        options = options.Validate();
        var report = options.Scenario switch
        {
            "server-mutation" => await ServerMutationSuite.RunAsync(harness, options, cancellationToken).ConfigureAwait(false),
            "storage-stall" => await StorageStallSuite.RunAsync(harness, options, cancellationToken).ConfigureAwait(false),
            "disk-pressure" => await DiskPressureSuite.RunAsync(harness, options, cancellationToken).ConfigureAwait(false),
            "network-soak" => await NetworkSoakSuite.RunAsync(harness, options, cancellationToken).ConfigureAwait(false),
            _ => await RunKillLoopAsync(harness, options, cancellationToken).ConfigureAwait(false),
        };
        return report with { Replay = options.ToCommandLine() };
    }

    /// <summary>
    /// What one kill may cost (KASI-RES-001) when the kill is instant, as a power cut is: 2 s of data (the checkpoint interval and the
    /// write buffer's age, KASI-NFR-007) plus what sat in the socket buffers. This is the bound for gate evidence on a dedicated machine.
    /// </summary>
    public static long StrictRefetchBound(FaultInjectionOptions options) =>
        (2 * options.BytesPerSecond) + (options.ConnectionCount * SocketBufferBytesPerConnection);

    /// <summary>
    /// The bound for a kill the harness had to deliver. The child keeps downloading between the moment the harness decides to kill
    /// it (for a commit-kill, when the commit hook fired) and the moment it is gone, and none of that has a checkpoint; on a loaded
    /// runner that took up to two seconds. A checkpoint commit that ran slow (<paramref name="flushLatency"/>, the child's slowest) also
    /// postponed the checkpoint by that long. So the bound is the strict one plus both latencies of data and half a second for
    /// scheduler jitter. A resume that starts over still fails it by gigabytes once the download is past its first seconds.
    /// </summary>
    public static long RefetchBound(FaultInjectionOptions options, TimeSpan killLatency, TimeSpan flushLatency = default) =>
        StrictRefetchBound(options) + (long)((killLatency + flushLatency + _schedulerJitter).TotalSeconds * options.BytesPerSecond);

    private static async Task<FaultInjectionReport> RunKillLoopAsync(
        FaultHarness harness,
        FaultInjectionOptions options,
        CancellationToken cancellationToken)
    {
        var commitKill = options.Scenario == "commit-kill";
        var started = Stopwatch.StartNew();
        var random = new Random(options.Seed);
        var results = new List<FaultIterationResult>();
        var marker = Path.Combine(harness.Root, "commit.marker");
        var kills = 0;
        var downloads = 0;
        var verified = 0;
        var endedWithoutKill = 0;
        long maxRefetch = 0;
        string? error = null;
        string? lastSha = null;
        Job? job = null;
        string? contentSeed = null;

        void Fail(string message)
        {
            results.Add(new FaultIterationResult(results.Count, false, message, 0, 0, kills));
            error ??= message;
        }

        async Task<bool> FinishAsync()
        {
            // the child ended on its own: the file must be the generated content, then make room for the next download
            var path = Path.Combine(job!.Folder, job.FileName);
            var state = (await harness.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false))!.State;
            if (state != JobState.Completed)
            {
                Fail($"download {downloads} ended in state {state}, not Completed");
                return false;
            }

            lastSha = await FaultHarness.Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (lastSha != GeneratedContent.Sha256Hex(contentSeed!, options.DownloadSize))
            {
                Fail($"download {downloads} finished with a SHA-256 that does not match the generated content");
                return false;
            }

            verified++;
            File.Delete(path);
            job = null;
            return true;
        }

        while (kills < options.IterationCount && error is null)
        {
            if (job is null)
            {
                downloads++;
                contentSeed = $"fault-{options.Scenario}-{options.Seed}-{downloads}";
                job = harness.NewJob($"/files/{contentSeed}/{options.DownloadSize}", $"{contentSeed}.bin", options.ConnectionCount);
                await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            }

            var before = (await harness.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
            var servedBefore = harness.Host.Metrics.BytesServed;
            await DeleteWithRetryAsync(marker, cancellationToken).ConfigureAwait(false);
            var flushFile = Path.Combine(harness.Root, "flush.latency");
            await DeleteWithRetryAsync(flushFile, cancellationToken).ConfigureAwait(false);
            var environment = commitKill ? ChildEnvironment.CommitKill(marker, random.Next(1, 6)) : new Dictionary<string, string>();
            environment[ChildEnvironment.FlushFile] = flushFile;
            using var child = harness.StartChild(job.Id, options.BytesPerSecond, environment);
            var runClock = Stopwatch.StartNew();
            var hit = commitKill
                ? await WaitForMarkerOrExitAsync(child, marker, _commitWait, cancellationToken).ConfigureAwait(false)
                : !await FaultHarness.WaitOrTimeoutAsync(child, TimeSpan.FromSeconds(random.Next(1, options.MaxKillSeconds + 1)), cancellationToken).ConfigureAwait(false);

            if (!hit)
            {
                if (child.ExitCode != 0)
                {
                    Fail($"the child exited with code {child.ExitCode}");
                }
                else
                {
                    // every download so far ended before the kill could land (a too small file, or a hook that never fires)
                    if (++endedWithoutKill >= 3)
                    {
                        Fail($"{endedWithoutKill} downloads in a row ended before a kill could land; use a larger --size or a lower --rate");
                    }

                    await FinishAsync().ConfigureAwait(false);
                }

                continue;
            }

            endedWithoutKill = 0;
            // how long the child ran on after the harness meant to kill it: since the hook fired (commit-kill) plus the kill itself
            var sinceHook = commitKill && File.Exists(marker) ? TimeProvider.System.GetUtcNow() - File.GetLastWriteTimeUtc(marker) : TimeSpan.Zero;
            var killClock = Stopwatch.StartNew();
            FaultHarness.KillIfRunning(child);
            await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var killLatency = (sinceHook > TimeSpan.Zero ? sinceHook : TimeSpan.Zero) + killClock.Elapsed;
            var inFlight = ReadInFlight(ChildEnvironment.InFlightFile(flushFile), TimeProvider.System.GetUtcNow());
            var flushLatency = TimeSpan.FromTicks(Math.Max(ReadFlushLatency(flushFile).Ticks, inFlight.Ticks));
            var bound = RefetchBound(options, killLatency, flushLatency);
            kills++;
            SqliteConnection.ClearAllPools();

            var after = (await harness.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
            var served = harness.Host.Metrics.BytesServed - servedBefore;
            var refetched = Math.Max(0, served - Math.Max(0, after.DoneBytes - before.DoneBytes));
            maxRefetch = Math.Max(maxRefetch, refetched);
            var problem = await FaultHarness.RetryAsync(() => CheckDatabaseAsync(harness.DatabasePath, job.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (problem is null && commitKill && !File.Exists(marker))
            {
                problem = "the commit hook never fired, so the kill did not land inside a commit";
            }

            if (problem is null && refetched > bound)
            {
                problem = $"{refetched} B had to be fetched again, above the {bound} B bound (the kill took {killLatency.TotalSeconds:0.00} s to land, the slowest or still-running checkpoint {flushLatency.TotalSeconds:0.00} s, {inFlight.TotalSeconds:0.00} s of it unfinished at the kill; {Math.Max(0, after.DoneBytes - before.DoneBytes)} B of {served} B served had been saved after {runClock.Elapsed.TotalSeconds:0.0} s)";
            }

            results.Add(new FaultIterationResult(results.Count, problem is null, problem, served, refetched, kills, Math.Round(killLatency.TotalSeconds, 3), Math.Round(flushLatency.TotalSeconds, 3), Math.Max(0, after.DoneBytes - before.DoneBytes), Math.Round(inFlight.TotalSeconds, 3), Math.Round(runClock.Elapsed.TotalSeconds, 3)));
            error ??= problem;
        }

        if (error is null && job is not null)
        {
            // the budget of kills is spent: let the last child run to the end and verify what it wrote
            using var last = harness.StartChild(job.Id, options.BytesPerSecond);
            var limit = TimeSpan.FromSeconds((options.DownloadSize / options.BytesPerSecond * 2) + 120);
            if (!await FaultHarness.WaitOrTimeoutAsync(last, limit, cancellationToken).ConfigureAwait(false) || last.ExitCode != 0)
            {
                var exit = last.HasExited ? last.ExitCode : -1;
                FaultHarness.KillIfRunning(last);
                Fail($"the final child did not complete (exit {exit})");
            }
            else
            {
                await FinishAsync().ConfigureAwait(false);
            }
        }

        return new FaultInjectionReport(
            options.Scenario,
            options.Seed,
            kills,
            kills,
            verified,
            results.Count(r => !r.Success),
            harness.Host.Metrics.BytesServed,
            maxRefetch,
            error is null && verified == downloads && downloads > 0,
            lastSha,
            started.Elapsed,
            error,
            results);
    }

    /// <summary>
    /// How long the checkpoint the child was in the middle of when it died had been running: a flush stuck on a slow disk never
    /// completes, so the slowest completed commit cannot show it. Zero when the child was between checkpoints or the file is unreadable.
    /// </summary>
    internal static TimeSpan ReadInFlight(string path, DateTimeOffset now)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
                ? TimeSpan.FromTicks(Math.Max(0, now.UtcTicks - ticks))
                : TimeSpan.Zero;
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>The child's slowest checkpoint; zero when it wrote nothing, or was killed while writing and left a partial number.</summary>
    internal static TimeSpan ReadFlushLatency(string path)
    {
        try
        {
            return double.TryParse(File.ReadAllText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>After a kill the database must open, pass SQLite's own check and still agree with itself about the job's bytes.</summary>
    internal static async Task<string?> CheckDatabaseAsync(string path, JobId jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CheckCoreAsync(path, jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 10 && attempt < 50)
            {
                // busy, locked or an I/O error right after a kill: Windows may still be releasing the dead process's lock files, so
                // this is retried for a few seconds; damage persists, a lock does not
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                // a file SQLite cannot open at all is the clearest damage there is
                return $"the database cannot be read: {ex.Message}";
            }
        }
    }

    private static async Task<string?> CheckCoreAsync(string path, JobId jobId, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var integrity = (string?)await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (integrity != "ok")
            {
                return $"integrity_check returned '{integrity}'";
            }

            await using var sums = connection.CreateCommand();
            sums.CommandText =
                "SELECT j.done_bytes, COALESCE((SELECT SUM(written) FROM segment WHERE job_id = j.id), 0) FROM job j WHERE j.id = $id;";
            sums.Parameters.AddWithValue("$id", jobId.ToString());
            var reader = await sums.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return "the job row is gone";
                }

                var done = reader.GetInt64(0);
                var written = reader.GetInt64(1);
                return done == written ? null : $"done_bytes {done} disagrees with the segments' written bytes {written}";
            }
        }
    }


    /// <summary>The killed child may still hold the marker for a moment while Windows closes its handles.</summary>
    private static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True when the marker appeared (the child is stuck inside a commit) or when the wait ran out (the caller then kills the child and
    /// the iteration reports the missing hook); false only when the child exited first without writing the marker.
    /// </summary>
    private static async Task<bool> WaitForMarkerOrExitAsync(Process child, string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (File.Exists(marker))
            {
                return true;
            }

            if (child.HasExited)
            {
                return File.Exists(marker);
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        // never reached a commit: kill it so the iteration reports the missing hook instead of hanging
        return true;
    }
}
