using System.Security.Cryptography;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kasi.Scheduling.Tests;

public sealed class PeriodicSyncTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-sync-tests", Guid.NewGuid().ToString("N"));
    private TestServerHost? _host;

    public PeriodicSyncTests()
    {
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort test cleanup
        }
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-006")]
    public async Task SyncJobAsync_WhenJobNotCompleted_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var jobs = new JobRepository(db.Factory);
        var control = new RecordingControl();
        var checker = new RecordingChecker();
        using var sync = new PeriodicSyncCoordinator(jobs, control, checker);

        var job = MakeJob(JobId.New(TimeProvider.System), JobState.Downloading);
        await jobs.AddAsync(job, ct);

        var result = await sync.SyncJobAsync(job.Id, ct);

        Assert.False(result);
        Assert.Empty(checker.CheckedJobs);
        Assert.Empty(control.StartedJobs);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-006")]
    public async Task SyncJobAsync_WhenRemoteUnchanged_ReturnsFalseAndLeavesJobCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var jobs = new JobRepository(db.Factory);
        var control = new RecordingControl();
        var checker = new RecordingChecker { HasChanged = false };
        using var sync = new PeriodicSyncCoordinator(jobs, control, checker);

        var job = MakeJob(JobId.New(TimeProvider.System), JobState.Completed);
        await jobs.AddAsync(job, ct);

        var result = await sync.SyncJobAsync(job.Id, ct);

        Assert.False(result);
        Assert.Single(checker.CheckedJobs, j => j.Id == job.Id);
        Assert.Empty(checker.PreparedJobs);
        Assert.Empty(control.StartedJobs);

        var stored = await jobs.GetAsync(job.Id, ct);
        Assert.NotNull(stored);
        Assert.Equal(JobState.Completed, stored.State);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-006")]
    public async Task SyncJobAsync_WhenRemoteChanged_ResetsProgressAndRequeuesAndStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var jobs = new JobRepository(db.Factory);
        var segments = new SegmentRepository(db.Factory);
        var control = new RecordingControl();
        var checker = new RecordingChecker { HasChanged = true };
        using var sync = new PeriodicSyncCoordinator(jobs, control, checker, segments);

        var job = MakeJob(JobId.New(TimeProvider.System), JobState.Completed);
        await jobs.AddAsync(job, ct);
        await jobs.ReconcileProgressAsync(job.Id, 500, ct);

        var result = await sync.SyncJobAsync(job.Id, ct);

        Assert.True(result);
        Assert.Single(checker.PreparedJobs, id => id == job.Id);
        Assert.Single(control.StartedJobs, id => id == job.Id);

        var stored = await jobs.GetAsync(job.Id, ct);
        Assert.NotNull(stored);
        Assert.Equal(JobState.Queued, stored.State);
        Assert.Equal(0, stored.DoneBytes);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-006")]
    public async Task SyncQueueAsync_ChecksOnlyCompletedJobsInQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var jobs = new JobRepository(db.Factory);
        var queues = new QueueRepository(db.Factory);
        var q2 = await queues.AddAsync(new Queue { Id = 2, Name = "Queue 2", SortOrder = 2 }, ct);
        var control = new RecordingControl();
        var checker = new RecordingChecker { HasChanged = true };
        using var sync = new PeriodicSyncCoordinator(jobs, control, checker);

        var job1 = MakeJob(JobId.New(TimeProvider.System), JobState.Completed, queueId: 1, pos: 1);
        var job2 = MakeJob(JobId.New(TimeProvider.System), JobState.Downloading, queueId: 1, pos: 2);
        var job3 = MakeJob(JobId.New(TimeProvider.System), JobState.Completed, queueId: q2, pos: 1);
        await jobs.AddAsync(job1, ct);
        await jobs.AddAsync(job2, ct);
        await jobs.AddAsync(job3, ct);

        var reDownloaded = await sync.SyncQueueAsync(1, ct);

        Assert.Equal(1, reDownloaded);
        Assert.Single(checker.CheckedJobs, j => j.Id == job1.Id);
        Assert.Equal([job1.Id], control.StartedJobs);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-006")]
    public async Task PeriodicTimer_TriggersPeriodicSyncAndRaisesEvent()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var jobs = new JobRepository(db.Factory);
        var control = new RecordingControl();
        var checker = new RecordingChecker { HasChanged = true };
        var fakeTime = new FakeTimeProvider();
        using var sync = new PeriodicSyncCoordinator(jobs, control, checker, timeProvider: fakeTime);

        var job = MakeJob(JobId.New(fakeTime), JobState.Completed);
        await jobs.AddAsync(job, TestContext.Current.CancellationToken);

        int completedEvents = 0;
        int syncedCount = 0;
        sync.SyncRunCompleted += (_, args) =>
        {
            completedEvents++;
            syncedCount += args.ReDownloadedCount;
        };

        sync.Start(TimeSpan.FromMinutes(1));
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // give background callback a moment to finish
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, completedEvents);
        Assert.Equal(1, syncedCount);
        Assert.Single(control.StartedJobs, id => id == job.Id);
    }

    [Fact]
    [Trait("TC", "TC-QUE-008")]
    [Trait("Requirement", "KASI-QUE-006")]
    [Trait("Category", "Integration")]
    public async Task TC_QUE_008_Change_of_mutable_content_triggers_one_redownload_unchanged_triggers_none()
    {
        var ct = TestContext.Current.CancellationToken;
        _host = await TestServerHost.StartAsync(cancellationToken: ct);

        var fileId = "sync-doc-" + Guid.NewGuid().ToString("N")[..8];
        const int size = (int)MutableFiles.DefaultSize;
        var folder = Path.Combine(_root, "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var factory = new SqliteConnectionFactory(Path.Combine(folder, "engine.db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var segments = new SegmentRepository(factory);
        await using var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();

        var bandwidth = new BandwidthLimiter();
        await using var engine = new DownloadEngine(jobs, segments, checkpoints, bandwidth, timeProvider: TimeProvider.System);
        await engine.StartAsync(ct);

        var control = engine;
        var checker = engine;
        using var coordinator = new PeriodicSyncCoordinator(jobs, control, checker, segments);

        // 1. Initial download
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = new Uri(_host.BaseAddress, $"/mutable/{fileId}").AbsoluteUri,
            FileName = $"{fileId}.bin",
            Folder = folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
            Connections = 2,
            TotalBytes = null,
        };

        await engine.AddJobAsync(job, cancellationToken: ct);
        await engine.StartJobAsync(job.Id, ct);
        await engine.WaitForJobAsync(job.Id, ct);

        var initialStored = (await jobs.GetAsync(job.Id, ct))!;
        Assert.Equal(JobState.Completed, initialStored.State);
        Assert.NotNull(initialStored.ETag);
        Assert.Equal(size, initialStored.DoneBytes);

        var targetFilePath = Path.Combine(folder, $"{fileId}.bin");
        Assert.True(File.Exists(targetFilePath));
        var initialBytes = await File.ReadAllBytesAsync(targetFilePath, ct);
        Assert.Equal(size, initialBytes.Length);
        var initialSha = Convert.ToHexString(SHA256.HashData(initialBytes));

        // 2. First sync check: unchanged triggers none (0 re-downloads)
        var unchangedSync = await coordinator.SyncJobAsync(job.Id, ct);
        Assert.False(unchangedSync);

        var afterUnchangedCheck = (await jobs.GetAsync(job.Id, ct))!;
        Assert.Equal(JobState.Completed, afterUnchangedCheck.State);
        Assert.Equal(initialStored.ETag, afterUnchangedCheck.ETag);

        // 3. Mutate remote content
        _host.Mutable.Change(fileId);

        // 4. Second sync check: changed triggers one re-download
        var changedSync = await coordinator.SyncJobAsync(job.Id, ct);
        Assert.True(changedSync);

        // Wait for re-download to complete
        await engine.WaitForJobAsync(job.Id, ct);

        var finalStored = (await jobs.GetAsync(job.Id, ct))!;
        Assert.Equal(JobState.Completed, finalStored.State);
        Assert.NotNull(finalStored.ETag);
        Assert.NotEqual(initialStored.ETag, finalStored.ETag);

        var updatedBytes = await File.ReadAllBytesAsync(targetFilePath, ct);
        var updatedSha = Convert.ToHexString(SHA256.HashData(updatedBytes));
        Assert.NotEqual(initialSha, updatedSha);

        // 5. Subsequent sync check: unchanged again triggers none
        var thirdSync = await coordinator.SyncJobAsync(job.Id, ct);
        Assert.False(thirdSync);
    }

    private static Job MakeJob(JobId id, JobState state, int queueId = 1, double pos = 1.0) => new()
    {
        Id = id,
        Kind = JobKind.Http,
        Url = "https://example.com/test-file.bin",
        FileName = "test-file.bin",
        Folder = "C:\\Downloads",
        QueueId = queueId,
        QueuePos = pos,
        CreatedUtc = TimeProvider.System.GetUtcNow(),
        State = state,
        TotalBytes = 1000,
        DoneBytes = state == JobState.Completed ? 1000 : 0,
        ETag = "\"v1\"",
        LastModified = "Wed, 01 Jan 2026 00:00:00 GMT",
    };

    private sealed class RecordingControl : IJobControl
    {
        public List<JobId> StartedJobs { get; } = [];
        public List<JobId> PausedJobs { get; } = [];

        public Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            StartedJobs.Add(id);
            return Task.CompletedTask;
        }

        public Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            PausedJobs.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChecker : IRemoteFileChecker
    {
        public bool HasChanged { get; set; } = true;
        public List<Job> CheckedJobs { get; } = [];
        public List<JobId> PreparedJobs { get; } = [];

        public Task<bool> HasRemoteChangedAsync(Job job, CancellationToken cancellationToken = default)
        {
            CheckedJobs.Add(job);
            return Task.FromResult(HasChanged);
        }

        public void PrepareForRedownload(JobId jobId)
        {
            PreparedJobs.Add(jobId);
        }
    }
}
