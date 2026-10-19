using System.Globalization;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;

// Child process of the kill-loop tests: <database> <job id> <bytes per second>. It opens the engine (which recovers anything a
// previous, killed run left behind), starts or resumes the job and waits for it to end.
if (args.Length != 3
    || !JobId.TryParse(args[1], out var jobId)
    || !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesPerSecond))
{
    return 2;
}

var factory = new SqliteConnectionFactory(args[0]);
var jobs = new JobRepository(factory);
var segments = new SegmentRepository(factory);
await using var checkpoints = new CheckpointWriter(factory);
checkpoints.Start();
await using var engine = new DownloadEngine(jobs, segments, checkpoints, new BandwidthLimiter(bytesPerSecond));
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
