using System.Globalization;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;

if (args.Length != 5
    || !JobId.TryParse(args[3], out var jobId)
    || !long.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesPerSecond))
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
await engine.StartJobAsync(jobId);
return 0;
