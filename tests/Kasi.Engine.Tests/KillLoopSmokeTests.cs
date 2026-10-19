using System.Diagnostics;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

/// <summary>
/// Smoke version of the kill loop (KASI-RES-001, TC-RES-001): a child process that runs a throttled download through
/// <see cref="DownloadEngine"/> is killed at three points, every restart recovers and resumes, and the finished file is
/// verified. The full 100/200-iteration harness is M1.14.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
[Trait("TC", "TC-RES-001")]
public sealed class KillLoopSmokeTests : IAsyncLifetime
{
    private const int FileSize = 16 * 1024 * 1024;
    private const int BytesPerSecond = 2 * 1024 * 1024;
    private static readonly double[] _killAt = [0.30, 0.55, 0.80];

    private TestServerHost _host = null!;
    private string _root = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _root = Path.Combine(Path.GetTempPath(), "kasi-kill-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_child_killed_three_times_recovers_resumes_and_finishes_without_refetching_everything()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(folder);
        var database = Path.Combine(_root, "kill.db");
        var factory = new SqliteConnectionFactory(database);
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = new Uri(_host.BaseAddress, $"/files/kill-smoke/{FileSize}").AbsoluteUri,
            FileName = "kill-smoke.bin",
            Folder = folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            Connections = 4,
        };
        await jobs.AddAsync(job, ct);
        _host.ResetMetrics();

        foreach (var fraction in _killAt)
        {
            using var child = StartChild(database, job.Id);
            try
            {
                await EventuallyAsync(async () => (await jobs.GetAsync(job.Id, ct))?.DoneBytes >= (long)(FileSize * fraction), ct);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }

            await child.WaitForExitAsync(ct);
            SqliteConnection.ClearAllPools();
            var killed = (await jobs.GetAsync(job.Id, ct))!;
            killed.State.Should().NotBe(JobState.Completed, "the child was killed before it finished");
        }

        using (var last = StartChild(database, job.Id))
        {
            await last.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct);
            last.ExitCode.Should().Be(0, "the last child recovers, resumes and runs to the end");
        }

        SqliteConnection.ClearAllPools();
        var completed = (await jobs.GetAsync(job.Id, ct))!;
        completed.State.Should().Be(JobState.Completed);
        completed.PauseReason.Should().BeNull();
        (await Sha256FileAsync(Path.Combine(folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("kill-smoke", FileSize));

        // Each kill may cost at most the 2 s loss budget (checkpoint interval plus buffer age) of data plus what was already in
        // the socket buffers. Restarting from byte 0 after every kill would serve about 42 MiB; this run must stay well below.
        var allowed = FileSize + (_killAt.Length * ((2L * BytesPerSecond) + (2L * 1024 * 1024)));
        _host.Metrics.BytesServed.Should().BeLessThan(allowed, "a resume continues from the saved cursors instead of starting over");
    }

    private static Process StartChild(string database, JobId jobId)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(FindChildDll());
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(jobId.ToString());
        start.ArgumentList.Add(BytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
    }

    private static string FindChildDll()
    {
        var testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var matches = Directory.GetFiles(Path.Combine(testsRoot, "Kasi.FaultInjection", "bin"), "Kasi.FaultInjection.dll", SearchOption.AllDirectories);
        return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the child should checkpoint within 60 s");
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
