using System.Globalization;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;

namespace Kasi.FaultInjection;

/// <summary>
/// The child of the kill suites: opens the engine (which recovers anything a previous, killed run left behind), starts or resumes
/// the job and waits for it to end. Returns 0 only when the job is Completed.
/// </summary>
internal static class ChildRunner
{
    private static void WriteQuietly(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a virus scanner or the parent may hold the file for a moment; the latency is a measurement, never a reason to stop
        }
    }

    public static async Task<int> RunAsync(string database, JobId jobId, long bytesPerSecond)
    {
        var factory = new SqliteConnectionFactory(database);
        var jobs = new JobRepository(factory);
        var segments = new SegmentRepository(factory);
        await using var checkpoints = new CheckpointWriter(factory);

        // Commit kill (TC-RES-011): let a few commits through, then stop inside the next one, with its transaction open, and tell the
        // parent by creating the marker file. The parent kills this process there.
        var marker = Environment.GetEnvironmentVariable(ChildEnvironment.CommitMarker);
        if (marker is not null
            && int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.CommitsBefore), NumberStyles.Integer, CultureInfo.InvariantCulture, out var allowed))
        {
            var commits = 0;
            checkpoints.BeforeCommit = async () =>
            {
                if (Interlocked.Increment(ref commits) > allowed)
                {
                    await File.WriteAllTextAsync(marker, "in a commit");
                    await Task.Delay(Timeout.Infinite);
                }
            };
        }

        // Slowest checkpoint so far, for the parent's refetch bound: a slow flush delays the checkpoint, so more is lost to a kill.
        // A checkpoint is the part-file flush plus the database commit. One that is still running when the kill lands never
        // completes, so the in-flight file says which step it is in ("flush:start" or "commit:flushStart:commitStart", UTC ticks)
        // and the parent counts the time it has been running.
        var flushFile = Environment.GetEnvironmentVariable(ChildEnvironment.FlushFile);
        var engineDecorator = (Func<Job, HttpTransferOptions, HttpTransferOptions>?)null;
        if (flushFile is not null)
        {
            var slowest = TimeSpan.Zero;
            var inFlightFile = ChildEnvironment.InFlightFile(flushFile);
            var lastStart = 0L;
            engineDecorator = (_, options) => options with
            {
                FlushFaultInjector = () =>
                {
                    lastStart = TimeProvider.System.GetUtcNow().UtcTicks;
                    WriteQuietly(inFlightFile, $"flush:{lastStart.ToString(CultureInfo.InvariantCulture)}");
                    return null;
                },
            };
            var killHook = checkpoints.BeforeCommit;
            checkpoints.BeforeCommit = async () =>
            {
                // the part file is flushed by now: the checkpoint is in its database commit
                WriteQuietly(inFlightFile, string.Create(CultureInfo.InvariantCulture, $"commit:{lastStart}:{TimeProvider.System.GetUtcNow().UtcTicks}"));
                if (killHook is not null)
                {
                    await killHook().ConfigureAwait(false);
                }
            };
            checkpoints.CommitCompleted = commit =>
            {
                var took = lastStart == 0 ? commit : TimeSpan.FromTicks(TimeProvider.System.GetUtcNow().UtcTicks - lastStart);
                WriteQuietly(inFlightFile, "0");
                if (took > slowest)
                {
                    slowest = took;
                    WriteQuietly(flushFile, slowest.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
                }
            };
        }

        checkpoints.Start();
        await using var engine = new DownloadEngine(jobs, segments, checkpoints, new BandwidthLimiter(bytesPerSecond)) { OptionsDecorator = engineDecorator };

        // Storage stall: delay a few part-file writes and arm a short read-stall timeout. The stall guard must only watch the network
        // read, so a slow write must not cost a retry.
        if (int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.StallMilliseconds), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stallMs)
            && int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.StallCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stallCount)
            && int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.ReadStallMilliseconds), NumberStyles.Integer, CultureInfo.InvariantCulture, out var readStallMs))
        {
            var writes = 0;
            var flushDecorator = engineDecorator;
            engine.OptionsDecorator = (job, baseOptions) => (flushDecorator is null ? baseOptions : flushDecorator(job, baseOptions)) with
            {
                RetryPolicy = baseOptions.RetryPolicy with { ReadStallTimeout = TimeSpan.FromMilliseconds(readStallMs) },
                WriteFaultInjector = (_, _) =>
                {
                    // let the transfer settle first, then stall the next few writes
                    var n = Interlocked.Increment(ref writes);
                    if (n > 3 && n <= 3 + stallCount)
                    {
                        Thread.Sleep(stallMs);
                    }

                    return null;
                },
            };
        }

        await engine.StartAsync();
        var job = await jobs.GetAsync(jobId);
        switch (job?.State)
        {
            case JobState.Queued:
                await engine.StartJobAsync(jobId);
                break;
            case JobState.Paused:
                await engine.ResumeJobAsync(jobId);
                break;
            default:
                return job?.State == JobState.Completed ? 0 : 3;
        }

        await engine.WaitForJobAsync(jobId);
        return (await jobs.GetAsync(jobId))?.State == JobState.Completed ? 0 : 4;
    }
}
