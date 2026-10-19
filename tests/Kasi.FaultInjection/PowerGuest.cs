using System.Diagnostics;
using System.Globalization;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;

namespace Kasi.FaultInjection;

/// <summary>
/// The part of the power-cut suite (KASI-RES-002, KASI-RES-003, TC-RES-002, TC-RES-003) that runs inside the Hyper-V guest. The
/// host script powers the guest off in the middle of <c>run</c>, starts it again and calls <c>verify</c>. The test server uses a
/// fixed port and the content is generated, so everything a restarted guest needs is in the state folder.
/// <list type="bullet">
/// <item><c>run</c>: create the job on first use, serve the content and download it until the job is Completed.</item>
/// <item><c>progress</c>: print how many percent the job has checkpointed (the host decides when to cut).</item>
/// <item><c>verify</c>: after a cut, the database must pass <c>integrity_check</c> and agree with itself; then the download
/// must finish with the right SHA-256. The verdict is written to <c>power-report.json</c> in the state folder.</item>
/// </list>
/// </summary>
internal static class PowerGuest
{
    public const int Port = 18080;
    private const string ContentSeed = "fault-power";

    public static async Task<int> RunAsync(FaultInjectionOptions options, CancellationToken cancellationToken)
    {
        var state = Path.GetFullPath(options.State!);
        Directory.CreateDirectory(state);
        var database = Path.Combine(state, "fault.db");
        var jobFile = Path.Combine(state, "job.id");
        var factory = new SqliteConnectionFactory(database);
        var jobs = new JobRepository(factory);

        if (options.Phase == "progress")
        {
            var percent = 0L;
            if (await ReadJobIdAsync(jobFile, cancellationToken).ConfigureAwait(false) is { } known
                && await jobs.GetAsync(known, cancellationToken).ConfigureAwait(false) is { } job)
            {
                percent = job.DoneBytes * 100 / options.DownloadSize;
            }

            await Console.Out.WriteLineAsync(percent.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return 0;
        }

        var started = Stopwatch.StartNew();
        string? problem = null;
        JobId jobId;
        if (await ReadJobIdAsync(jobFile, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            jobId = existing;
            if (options.Phase == "verify")
            {
                problem = await FaultInjectionRunner.CheckDatabaseAsync(database, jobId, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await new MigrationRunner(factory).MigrateAsync(cancellationToken).ConfigureAwait(false);
            var folder = Path.Combine(state, "downloads");
            Directory.CreateDirectory(folder);
            var created = new Job
            {
                Id = JobId.New(TimeProvider.System),
                Kind = JobKind.Http,
                Url = $"http://127.0.0.1:{Port}/files/{ContentSeed}/{options.DownloadSize}",
                FileName = "power.bin",
                Folder = folder,
                QueueId = 1,
                QueuePos = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                Connections = options.ConnectionCount,
            };
            await jobs.AddAsync(created, cancellationToken).ConfigureAwait(false);
            jobId = created.Id;
            await File.WriteAllTextAsync(jobFile, jobId.ToString(), cancellationToken).ConfigureAwait(false);
        }

        // a database that failed its check goes straight to the report: resuming on it could crash instead of report
        var exit = 3;
        if (problem is null)
        {
            var host = await TestServerHost.StartAsync(new TestServerOptions { Port = Port }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (host.ConfigureAwait(false))
            {
                exit = await ChildRunner.RunAsync(database, jobId, options.BytesPerSecond).ConfigureAwait(false);
            }
        }

        if (options.Phase != "verify")
        {
            return exit;
        }

        var sha = string.Empty;
        if (problem is null)
        {
            var path = Path.Combine(state, "downloads", "power.bin");
            if (exit != 0 || !File.Exists(path))
            {
                problem = $"after the cut the download did not complete (exit {exit})";
            }
            else
            {
                sha = await FaultHarness.Sha256Async(path, cancellationToken).ConfigureAwait(false);
                problem = sha == GeneratedContent.Sha256Hex(ContentSeed, options.DownloadSize) ? null : "the finished file does not match the generated content";
            }
        }

        var report = new FaultInjectionReport(
            "power-cut",
            options.Seed,
            1,
            1,
            problem is null ? 1 : 0,
            problem is null ? 0 : 1,
            0,
            0,
            problem is null,
            sha.Length == 0 ? null : sha,
            started.Elapsed,
            problem,
            [new FaultIterationResult(0, problem is null, problem, 0, 0, 1)]);
        await report.WriteAsync(Path.Combine(state, "power-report.json"), cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(problem is null ? "PASS" : $"FAIL: {problem}").ConfigureAwait(false);
        return problem is null ? 0 : 1;
    }

    private static async Task<JobId?> ReadJobIdAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) && JobId.TryParse((await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim(), out var id) ? id : null;
}
