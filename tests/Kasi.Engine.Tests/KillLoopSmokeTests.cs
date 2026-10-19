using System.Diagnostics;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
[Trait("TC", "TC-RES-001")]
public sealed class KillLoopSmokeTests : IAsyncLifetime
{
    private const int FileSize = 16 * 1024 * 1024;
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
    public async Task A_killed_child_is_recovered_and_resumed_without_mixed_content()
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
        var childDll = FindChildDll();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        start.ArgumentList.Add(childDll);
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(job.Url);
        start.ArgumentList.Add(folder);
        start.ArgumentList.Add(job.Id.ToString());
        start.ArgumentList.Add((2 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var child = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        try
        {
            await EventuallyAsync(async () => (await jobs.GetAsync(job.Id, ct))?.DoneBytes > 0, ct);
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

        var segments = new SegmentRepository(factory);
        var before = (await jobs.GetAsync(job.Id, ct))!;
        var beforeSegments = await segments.LoadAsync(job.Id, ct);
        await using var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();
        await using var engine = new DownloadEngine(jobs, segments, checkpoints, new BandwidthLimiter());
        await engine.StartAsync(ct);

        var recovered = (await jobs.GetAsync(job.Id, ct))!;
        var partPath = Path.Combine(folder, job.FileName + PartFileWriter.PartExtension);
        var savedSegments = await segments.LoadAsync(job.Id, ct);
        if (recovered.State != JobState.Paused)
        {
            throw new InvalidOperationException(
                $"before={before.State}/{before.TotalBytes}/{before.Resumable} beforeSegments={beforeSegments.Count} " +
                $"beforeRanges={string.Join(';', beforeSegments.Select(s => $"{s.Index}:{s.Start}-{s.End}:{s.Written}"))} " +
                $"beforeWritten={beforeSegments.Sum(s => s.Written)} beforeDone={before.DoneBytes} " +
                $"state={recovered.State} total={recovered.TotalBytes} part={File.Exists(partPath)} " +
                $"length={(File.Exists(partPath) ? new FileInfo(partPath).Length : -1)} segments={savedSegments.Count} " +
                $"written={savedSegments.Sum(s => s.Written)}");
        }

        recovered.State.Should().Be(JobState.Paused);
        recovered.PauseReason.Should().Be(PauseReason.CrashRecovery);
        await engine.ResumeJobAsync(job.Id, ct);

        var completed = (await jobs.GetAsync(job.Id, ct))!;
        completed.State.Should().Be(JobState.Completed);
        (await Sha256FileAsync(Path.Combine(folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("kill-smoke", FileSize));
    }

    private static string FindChildDll()
    {
        var testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var matches = Directory.GetFiles(Path.Combine(testsRoot, "Kasi.FaultInjection", "bin"), "Kasi.FaultInjection.dll", SearchOption.AllDirectories);
        return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the child should checkpoint within 30 s");
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
