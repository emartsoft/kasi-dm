using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Microsoft.Data.Sqlite;

namespace Kasi.Bench.Macro;

/// <summary>
/// <c>resources</c>: engine-only early signals for KASI-NFR-004 and KASI-NFR-005 (TPL 6.3). Idle: an engine with no jobs.
/// Twenty jobs: twenty concurrent downloads in one engine, held active by a global cap. Load: one job at about 100 MB/s. The
/// numbers are reported against the targets; they inform, and the whole-app gate comes with the app (M2 and later).
/// </summary>
public static class ResourceRunner
{
    public const double IdleLimitMegabytes = 80;
    public const double TwentyJobsLimitMegabytes = 250;
    public const double CpuLimitPercent = 5;

    public static async Task<int> RunAsync(string scenario, int idleSeconds, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            return scenario switch
            {
                "baseline" => Report("sampler only", await BaselineAsync(idleSeconds, cancellationToken).ConfigureAwait(false), IdleLimitMegabytes, CpuLimitPercent),
                "idle" => Report("idle engine", await IdleAsync(folder, idleSeconds, cancellationToken).ConfigureAwait(false), IdleLimitMegabytes, null),
                "jobs20" => Report("20 active jobs", await ManyJobsAsync(folder, 20, 64L * 1024 * 1024, 40L * 1024 * 1024, cancellationToken).ConfigureAwait(false), TwentyJobsLimitMegabytes, null),
                "load100" => Report("about 100 MB/s", await ManyJobsAsync(folder, 1, 2L * 1024 * 1024 * 1024, 100L * 1000 * 1000, cancellationToken).ConfigureAwait(false), null, CpuLimitPercent),
                _ => throw new ArgumentException($"Unknown scenario '{scenario}' (baseline, idle, jobs20 or load100)."),
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a handle can outlive the run by a moment
            }
        }
    }

    private static int Report(string name, ResourceReading reading, double? memoryLimit, double? cpuLimit)
    {
        var memoryOk = memoryLimit is null || reading.PeakWorkingSetMegabytes < memoryLimit;
        var cpuOk = cpuLimit is null || reading.CpuPercentOfOneCore < cpuLimit;
        Console.WriteLine($"{name}: {ResourceSampler.Describe(reading)} → {(memoryOk && cpuOk ? "within the target" : "OVER the target")} (engine only; an early signal)");
        return memoryOk && cpuOk ? 0 : 1;
    }

    private static async Task<ResourceReading> BaselineAsync(int seconds, CancellationToken cancellationToken)
    {
        await using var sampler = new ResourceSampler();
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        return await sampler.StopAsync().ConfigureAwait(false);
    }

    private static async Task<ResourceReading> IdleAsync(string folder, int seconds, CancellationToken cancellationToken)
    {
        var (engine, checkpoints, _) = await OpenAsync(folder, null, cancellationToken).ConfigureAwait(false);
        await using (checkpoints.ConfigureAwait(false))
        await using (engine.ConfigureAwait(false))
        {
            await using var sampler = new ResourceSampler();
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            return await sampler.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ResourceReading> ManyJobsAsync(string folder, int jobs, long size, long globalBytesPerSecond, CancellationToken cancellationToken)
    {
        await using var server = await ServerProcess.StartAsync(cancellationToken).ConfigureAwait(false);
        var (engine, checkpoints, repository) = await OpenAsync(folder, globalBytesPerSecond, cancellationToken).ConfigureAwait(false);
        await using (checkpoints.ConfigureAwait(false))
        await using (engine.ConfigureAwait(false))
        {
            var added = new List<JobId>();
            for (var i = 0; i < jobs; i++)
            {
                var job = new Job
                {
                    Id = JobId.New(TimeProvider.System),
                    Kind = JobKind.Http,
                    Url = new Uri(server.BaseAddress, $"files/resources-{i}/{size}").AbsoluteUri,
                    FileName = $"resources-{i}.bin",
                    Folder = folder,
                    QueueId = 1,
                    QueuePos = i + 1,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Connections = jobs == 1 ? 16 : 4,
                };
                await engine.AddJobAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false);
                added.Add(job.Id);
            }

            await using var sampler = new ResourceSampler();
            await Task.WhenAll(added.Select(async id =>
            {
                await engine.StartJobAsync(id, cancellationToken).ConfigureAwait(false);
                await engine.WaitForJobAsync(id, cancellationToken).ConfigureAwait(false);
            })).ConfigureAwait(false);
            var reading = await sampler.StopAsync().ConfigureAwait(false);
            foreach (var id in added)
            {
                if ((await repository.GetAsync(id, cancellationToken).ConfigureAwait(false))?.State != JobState.Completed)
                {
                    throw new InvalidOperationException("A resource-run job did not complete.");
                }
            }

            return reading;
        }
    }

    private static async Task<(DownloadEngine Engine, CheckpointWriter Checkpoints, JobRepository Jobs)> OpenAsync(string folder, long? bytesPerSecond, CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(folder, "resources.db"));
        await new MigrationRunner(factory).MigrateAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new JobRepository(factory);
        var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();
        var engine = new DownloadEngine(jobs, new SegmentRepository(factory), checkpoints, new BandwidthLimiter(bytesPerSecond));
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        return (engine, checkpoints, jobs);
    }
}
