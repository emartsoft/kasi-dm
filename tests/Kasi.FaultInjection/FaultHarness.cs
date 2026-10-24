using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.FaultInjection;

/// <summary>
/// What every suite shares: a test server, a database in a temp folder, a way to add a job for it, and helpers to run, wait for
/// and kill the child process that downloads it.
/// </summary>
internal sealed class FaultHarness : IAsyncDisposable
{
    private FaultHarness(string root, string databasePath, TestServerHost host, JobRepository jobs)
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

    public static async Task<FaultHarness> CreateAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "kasi-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = await TestServerHost.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var database = Path.Combine(root, "fault.db");
        var factory = new SqliteConnectionFactory(database);
        await new MigrationRunner(factory).MigrateAsync(cancellationToken).ConfigureAwait(false);
        return new FaultHarness(root, database, host, new JobRepository(factory));
    }

    /// <summary>
    /// A queued job for <paramref name="path"/> on the test server, or on <paramref name="server"/> when the job should reach it through
    /// a proxy; <paramref name="folder"/> defaults to a folder under the root.
    /// </summary>
    public Job NewJob(string path, string fileName, int connections, string? folder = null, Uri? server = null)
    {
        folder ??= Path.Combine(Root, "downloads");
        Directory.CreateDirectory(folder);
        return new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = new Uri(server ?? Host.BaseAddress, path).AbsoluteUri,
            FileName = fileName,
            Folder = folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            Connections = connections,
        };
    }

    /// <summary>Starts the child on <paramref name="jobId"/>; <paramref name="environment"/> arms its test hooks (see <see cref="ChildEnvironment"/>).</summary>
    public Process StartChild(JobId jobId, long bytesPerSecond, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Root,
        };
        start.ArgumentList.Add(FindChildDll());
        start.ArgumentList.Add(DatabasePath);
        start.ArgumentList.Add(jobId.ToString());
        start.ArgumentList.Add(bytesPerSecond.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The fault child did not start.");
    }

    /// <summary>The child sits next to this assembly when the harness runs itself, and in the child project's output when a test project runs it.</summary>
    public static string FindChildDll()
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

    public static async Task<bool> WaitOrTimeoutAsync(Process child, TimeSpan timeout, CancellationToken cancellationToken)
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

    public static void KillIfRunning(Process child)
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

    /// <summary>
    /// Reads a job, trying again for a few seconds when SQLite reports an I/O or lock error: right after a child was killed, Windows can
    /// take a moment to release the database's lock and shared-memory files.
    /// </summary>
    public Task<Job?> GetJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        RetryAsync(() => Jobs.GetAsync(jobId, cancellationToken), cancellationToken);

    public static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SqliteException) when (attempt < 100)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits until the job has restarted at least <paramref name="restarts"/> times and has a checkpoint between 1 byte and
    /// <paramref name="belowFraction"/> of <paramref name="size"/>; false if the child ended first.
    /// </summary>
    public async Task<bool> WaitForProgressAsync(Process child, JobId jobId, long size, double belowFraction, int restarts, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        Job? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(60) && !child.HasExited)
        {
            var job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            last = job ?? last;
            if (job is { DoneBytes: > 0 } && job.RestartCount >= restarts && job.DoneBytes < size * belowFraction)
            {
                return true;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        // a miss has to say why: the child finished before the window, or the window never opened
        LastProgressNote = $"waited {deadline.Elapsed.TotalSeconds:0.0} s, child {(child.HasExited ? $"exited with {child.ExitCode}" : "still running")}, "
            + $"last seen {(last is null ? "no job" : $"{last.DoneBytes} of {size} bytes, {last.RestartCount} restart(s), state {last.State}")}";
        return false;
    }

    /// <summary>What <see cref="WaitForProgressAsync"/> saw when it returned false (for the failure message).</summary>
    public string LastProgressNote { get; private set; } = string.Empty;

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
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

/// <summary>Environment variables the harness sets on the child to arm its test hooks, and the child reads in <c>Program</c>.</summary>
internal static class ChildEnvironment
{
    public const string FlushFile = "KASI_FAULT_FLUSH_FILE";
    /// <summary>Where the child writes the start of the checkpoint it is in the middle of (UTC ticks), or 0 between checkpoints.</summary>
    public static string InFlightFile(string flushFile) => flushFile + ".inflight";

    public const string CommitMarker = "KASI_FAULT_COMMIT_MARKER";
    public const string CommitsBefore = "KASI_FAULT_COMMITS_BEFORE";
    public const string StallMilliseconds = "KASI_FAULT_STALL_MS";
    public const string StallCount = "KASI_FAULT_STALL_COUNT";
    public const string ReadStallMilliseconds = "KASI_FAULT_READ_STALL_MS";

    /// <summary>Let <paramref name="commitsBefore"/> commits through, then stop inside the next one and create <paramref name="marker"/>.</summary>
    public static Dictionary<string, string> CommitKill(string marker, int commitsBefore) => new()
    {
        [CommitMarker] = marker,
        [CommitsBefore] = commitsBefore.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Delay the first <paramref name="count"/> part-file writes by <paramref name="stall"/> and arm the read-stall guard at <paramref name="readStall"/>.</summary>
    public static Dictionary<string, string> StorageStall(TimeSpan stall, int count, TimeSpan readStall) => new()
    {
        [StallMilliseconds] = ((int)stall.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        [StallCount] = count.ToString(CultureInfo.InvariantCulture),
        [ReadStallMilliseconds] = ((int)readStall.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
    };
}
