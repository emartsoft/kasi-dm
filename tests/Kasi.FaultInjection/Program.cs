using System.Globalization;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.FaultInjection;

// Two modes. With three arguments (<database> <job id> <bytes per second>) this is the child the kill loops start: it opens the
// engine (which recovers anything a previous, killed run left behind), starts or resumes the job and waits for it to end.
// Otherwise it is the harness itself: it parses the options and runs a suite (see FaultInjectionOptions.Usage).
if (args.Length == 3
    && JobId.TryParse(args[1], out var childJob)
    && long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesPerSecond))
{
    return await RunChildAsync(args[0], childJob, bytesPerSecond);
}

try
{
    return await FaultInjectionRunner.RunAsync(FaultInjectionOptions.Parse(args));
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    await Console.Error.WriteLineAsync(FaultInjectionOptions.Usage);
    return 2;
}

static async Task<int> RunChildAsync(string database, JobId jobId, long bytesPerSecond)
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
