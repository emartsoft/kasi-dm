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

        // Slowest commit so far, for the parent's refetch bound: a slow flush delays the checkpoint, so more is lost to a kill.
        var flushFile = Environment.GetEnvironmentVariable(ChildEnvironment.FlushFile);
        if (flushFile is not null)
        {
            var slowest = TimeSpan.Zero;
            checkpoints.CommitCompleted = took =>
            {
                if (took > slowest)
                {
                    slowest = took;
                    try
                    {
                        File.WriteAllText(flushFile, slowest.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a virus scanner or the parent may hold the file for a moment; the latency is a measurement, never a reason to stop
                    }
                }
            };
        }

        checkpoints.Start();
        await using var engine = new DownloadEngine(jobs, segments, checkpoints, new BandwidthLimiter(bytesPerSecond));

        // Storage stall: delay a few part-file writes and arm a short read-stall timeout. The stall guard must only watch the network
        // read, so a slow write must not cost a retry.
        if (int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.StallMilliseconds), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stallMs)
            && int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.StallCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stallCount)
            && int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.ReadStallMilliseconds), NumberStyles.Integer, CultureInfo.InvariantCulture, out var readStallMs))
        {
            var writes = 0;
            engine.OptionsDecorator = (_, options) => options with
            {
                RetryPolicy = options.RetryPolicy with { ReadStallTimeout = TimeSpan.FromMilliseconds(readStallMs) },
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
