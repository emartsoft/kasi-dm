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

    public static async Task<int> RunAsync(FaultInjectionOptions requested, CancellationToken cancellationToken = default)
    {
        if (requested.Help)
        {
            await Console.Out.WriteLineAsync(FaultInjectionOptions.Usage).ConfigureAwait(false);
            return 0;
        }

        var options = requested.Validate();
        var report = await RunToReportAsync(options, cancellationToken).ConfigureAwait(false);
        await report.WriteAsync(options.ReportPath, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"{report.Scenario}: seed={report.Seed} kills={report.Kills} downloads-verified={report.Completed} " +
            $"failed={report.Failed} max-refetch={report.MaxRefetchedBytes} duration={report.Duration:g}").ConfigureAwait(false);
        if (!report.Passed)
        {
            await Console.Error.WriteLineAsync($"FAILED: {report.Error ?? "see the report"}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Re-run: {report.ToCommandLine()}").ConfigureAwait(false);
        }

        return report.Passed ? 0 : 1;
    }

    public static async Task<FaultInjectionReport> RunToReportAsync(FaultInjectionOptions options, CancellationToken cancellationToken = default)
    {
        await using var harness = await Harness.CreateAsync(cancellationToken).ConfigureAwait(false);
        return await RunKillLoopAsync(harness, options.Validate(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What one kill may cost (KASI-RES-001): 2 s of data (the checkpoint interval) plus what sat in the socket buffers. A resume
    /// that starts over instead fails this by gigabytes once the download is past its first seconds.
    /// </summary>
    public static long RefetchBound(FaultInjectionOptions options) =>
        (2 * options.BytesPerSecond) + (options.Connections * SocketBufferBytesPerConnection);

    private static async Task<FaultInjectionReport> RunKillLoopAsync(
        Harness harness,
        FaultInjectionOptions options,
        CancellationToken cancellationToken)
    {
        var commitKill = options.Scenario == "commit-kill";
        var started = Stopwatch.StartNew();
        var random = new Random(options.Seed);
        var results = new List<FaultIterationResult>();
        var marker = Path.Combine(harness.Root, "commit.marker");
        var bound = RefetchBound(options);
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
            var state = (await harness.Jobs.GetAsync(job.Id, cancellationToken).ConfigureAwait(false))!.State;
            if (state != JobState.Completed)
            {
                Fail($"download {downloads} ended in state {state}, not Completed");
                return false;
            }

            lastSha = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
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
                job = harness.NewJob($"/files/{contentSeed}/{options.DownloadSize}", $"{contentSeed}.bin", options.Connections);
                await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            }

            var before = (await harness.Jobs.GetAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
            var servedBefore = harness.Host.Metrics.BytesServed;
            File.Delete(marker);
            using var child = StartChild(harness, options, job.Id, commitKill ? new CommitKill(marker, random.Next(1, 6)) : null);
            var hit = commitKill
                ? await WaitForMarkerOrExitAsync(child, marker, _commitWait, cancellationToken).ConfigureAwait(false)
                : !await WaitOrTimeoutAsync(child, TimeSpan.FromSeconds(random.Next(1, options.MaxKillSeconds + 1)), cancellationToken).ConfigureAwait(false);

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
            KillIfRunning(child);
            await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            kills++;
            SqliteConnection.ClearAllPools();

            var after = (await harness.Jobs.GetAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
            var served = harness.Host.Metrics.BytesServed - servedBefore;
            var refetched = Math.Max(0, served - Math.Max(0, after.DoneBytes - before.DoneBytes));
            maxRefetch = Math.Max(maxRefetch, refetched);
            var problem = await CheckDatabaseAsync(harness.DatabasePath, job.Id, cancellationToken).ConfigureAwait(false);
            if (problem is null && commitKill && !File.Exists(marker))
            {
                problem = "the commit hook never fired, so the kill did not land inside a commit";
            }

            if (problem is null && refetched > bound)
            {
                problem = $"{refetched} B had to be fetched again, above the {bound} B bound";
            }

            results.Add(new FaultIterationResult(results.Count, problem is null, problem, served, refetched, kills));
            error ??= problem;
        }

        if (error is null && job is not null)
        {
            // the budget of kills is spent: let the last child run to the end and verify what it wrote
            using var last = StartChild(harness, options, job.Id, commit: null);
            var limit = TimeSpan.FromSeconds((options.DownloadSize / options.BytesPerSecond * 2) + 120);
            if (!await WaitOrTimeoutAsync(last, limit, cancellationToken).ConfigureAwait(false) || last.ExitCode != 0)
            {
                var exit = last.HasExited ? last.ExitCode : -1;
                KillIfRunning(last);
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

    /// <summary>After a kill the database must open, pass SQLite's own check and still agree with itself about the job's bytes.</summary>
    private static async Task<string?> CheckDatabaseAsync(string path, JobId jobId, CancellationToken cancellationToken)
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

    private sealed record CommitKill(string Marker, int CommitsBefore);

    private static Process StartChild(Harness harness, FaultInjectionOptions options, JobId jobId, CommitKill? commit)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = harness.Root,
        };
        start.ArgumentList.Add(FindChildDll());
        start.ArgumentList.Add(harness.DatabasePath);
        start.ArgumentList.Add(jobId.ToString());
        start.ArgumentList.Add(options.BytesPerSecond.ToString(CultureInfo.InvariantCulture));
        if (commit is not null)
        {
            start.Environment[ChildEnvironment.CommitMarker] = commit.Marker;
            start.Environment[ChildEnvironment.CommitsBefore] = commit.CommitsBefore.ToString(CultureInfo.InvariantCulture);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The fault child did not start.");
    }

    /// <summary>The child sits next to this assembly when the harness runs itself, and in the child project's output when a test project runs it.</summary>
    private static string FindChildDll()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "Kasi.FaultInjection.dll");
        if (File.Exists(Path.ChangeExtension(here, ".runtimeconfig.json")))
        {
            return here;
        }

        var testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Directory
            .GetFiles(Path.Combine(testsRoot, "Kasi.FaultInjection", "bin"), "Kasi.FaultInjection.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static async Task<bool> WaitOrTimeoutAsync(Process child, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await child.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>True when the marker appeared (the child is stuck inside a commit); false when the child ended first or the wait ran out.</summary>
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

    private static void KillIfRunning(Process child)
    {
        if (child.HasExited)
        {
            return;
        }

        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it exited between the check and the kill
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string root, string databasePath, TestServerHost host, JobRepository jobs)
        {
            Root = root;
            DatabasePath = databasePath;
            Host = host;
            Jobs = jobs;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public TestServerHost Host { get; }

        public JobRepository Jobs { get; }

        public static async Task<Harness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "kasi-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = await TestServerHost.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var database = Path.Combine(root, "fault.db");
            var factory = new SqliteConnectionFactory(database);
            await new MigrationRunner(factory).MigrateAsync(cancellationToken).ConfigureAwait(false);
            return new Harness(root, database, host, new JobRepository(factory));
        }

        public Job NewJob(string path, string fileName, int connections)
        {
            var folder = Path.Combine(Root, "downloads");
            Directory.CreateDirectory(folder);
            return new Job
            {
                Id = JobId.New(TimeProvider.System),
                Kind = JobKind.Http,
                Url = new Uri(Host.BaseAddress, path).AbsoluteUri,
                FileName = fileName,
                Folder = folder,
                QueueId = 1,
                QueuePos = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                Connections = connections,
            };
        }

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            await Host.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // a pooled SQLite handle can outlive the process by a moment
            }
        }
    }
}

/// <summary>Environment variables the harness sets on the child to arm the commit-kill hook.</summary>
internal static class ChildEnvironment
{
    public const string CommitMarker = "KASI_FAULT_COMMIT_MARKER";
    public const string CommitsBefore = "KASI_FAULT_COMMITS_BEFORE";
}
