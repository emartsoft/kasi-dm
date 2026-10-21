using System.Diagnostics;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Microsoft.Data.Sqlite;

namespace Kasi.Bench.Macro;

/// <summary>The outcome of one timed download through <see cref="DownloadEngine"/>.</summary>
public sealed record DownloadTiming(double Seconds, long Bytes, bool Completed, string FilePath);

/// <summary>
/// Runs one download through the real engine, in a database of its own, and times it from the call that starts the job to the
/// moment the job is Completed (the part file is sealed and renamed). Hashing the result is the caller's job and is not timed.
/// </summary>
public static class EngineDownload
{
    public static async Task<DownloadTiming> RunAsync(
        string url,
        string folder,
        string fileName,
        int connections,
        long? bytesPerSecond,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var database = Path.Combine(folder, $"bench-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(database);
        try
        {
            await new MigrationRunner(factory).MigrateAsync(cancellationToken).ConfigureAwait(false);
            var jobs = new JobRepository(factory);
            var segments = new SegmentRepository(factory);
            var checkpoints = new CheckpointWriter(factory);
            await using (checkpoints.ConfigureAwait(false))
            {
                checkpoints.Start();
                var engine = new DownloadEngine(jobs, segments, checkpoints, new BandwidthLimiter(bytesPerSecond));
                await using (engine.ConfigureAwait(false))
                {
                    await engine.StartAsync(cancellationToken).ConfigureAwait(false);
                    var job = new Job
                    {
                        Id = JobId.New(TimeProvider.System),
                        Kind = JobKind.Http,
                        Url = url,
                        FileName = fileName,
                        Folder = folder,
                        QueueId = 1,
                        QueuePos = 1,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        Connections = connections,
                    };
                    await engine.AddJobAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false);

                    var clock = Stopwatch.StartNew();
                    await engine.StartJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    await engine.WaitForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    clock.Stop();

                    var done = await jobs.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    var path = Path.Combine(folder, fileName);
                    var completed = done?.State == JobState.Completed && File.Exists(path);
                    return new DownloadTiming(clock.Elapsed.TotalSeconds, completed ? new FileInfo(path).Length : 0, completed, path);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(database);
        }
    }

    public static double Megabits(long bytes, double seconds) => seconds <= 0 ? 0 : bytes * 8d / 1_000_000d / seconds;

    private static void TryDelete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // a pooled handle can outlive the run by a moment; the temp folder is removed with the session
            }
        }
    }
}
