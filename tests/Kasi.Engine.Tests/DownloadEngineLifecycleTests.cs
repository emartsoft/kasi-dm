using System.Collections.Concurrent;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-019")]
[Trait("Requirement", "KASI-ENG-008")]
public sealed class DownloadEngineLifecycleTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _root = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _root = Path.Combine(Path.GetTempPath(), "kasi-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task A_headless_download_persists_each_transition_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/engine-download/4194304", "engine-download.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var states = new ConcurrentQueue<(JobState From, JobState To)>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged changed && changed.Id == job.Id)
            {
                states.Enqueue((changed.From, changed.To));
            }
        };

        await harness.Engine.StartJobAsync(job.Id, ct);
        await harness.Engine.StopAsync(ct);
        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.Error.Should().BeNull($"the engine stored state {stored.State}");

        states.Should().ContainInOrder(
            (JobState.Queued, JobState.Probing),
            (JobState.Probing, JobState.Downloading),
            (JobState.Downloading, JobState.Verifying),
            (JobState.Verifying, JobState.Completed));
        stored.State.Should().Be(JobState.Completed);
        stored.DoneBytes.Should().Be(4 * 1024 * 1024);
        stored.CompletedUtc.Should().NotBeNull();
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("engine-download", 4 * 1024 * 1024));
    }

    [Fact]
    public async Task A_state_changed_handler_sees_the_committed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/handler-row/2097152", "handler-row.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var seen = new TaskCompletionSource<JobState>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged { To: JobState.Completed } changed && changed.Id == job.Id)
            {
                _ = Task.Run(async () => seen.TrySetResult((await harness.Jobs.GetAsync(job.Id, ct))!.State), ct);
            }
        };

        await harness.Engine.StartJobAsync(job.Id, ct);

        (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().Be(JobState.Completed);
        await harness.Engine.StopAsync(ct);
    }

    [Fact]
    public async Task A_throwing_subscriber_is_counted_and_does_not_stop_the_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/throwing-subscriber/2097152", "throwing-subscriber.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.EventRaised += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Engine.StartJobAsync(job.Id, ct);
        await harness.Engine.StopAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
        harness.Engine.SubscriberFaultCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_duplicate_add_returns_the_conflict_without_adding_another_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/duplicate-add/1048576", "duplicate-add.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);

        var duplicate = job with { Id = JobId.New(TimeProvider.System) };
        var check = await harness.Engine.AddJobAsync(duplicate, cancellationToken: ct);

        check.HasConflict.Should().BeTrue();
        check.ExistingJob!.Id.Should().Be(job.Id);
        (await harness.Jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    [Trait("TC", "TC-RES-002")]
    public async Task Recovery_pauses_a_job_with_a_valid_part_and_reconciles_its_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-part/1048576", "recover-part.bin") with
        {
            State = JobState.Downloading,
            TotalBytes = 1024 * 1024,
            Resumable = true,
        };
        await harness.Jobs.AddAsync(job, ct);
        await harness.Segments.ReplaceAsync(job.Id, [new Segment(0, 0, 1024 * 1024, 4096)], ct);
        await File.WriteAllBytesAsync(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension), new byte[1024 * 1024], ct);
        var states = new ConcurrentQueue<JobState>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged changed && changed.Id == job.Id)
            {
                states.Enqueue(changed.To);
            }
        };

        await harness.Engine.StartAsync(ct);

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Paused);
        stored.PauseReason.Should().Be(PauseReason.CrashRecovery);
        stored.DoneBytes.Should().Be(4096);
        // events reach subscribers on the dispatcher thread, a moment after the transition committed
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!states.Contains(JobState.Paused) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        states.Should().Contain(JobState.Paused);
    }

    [Fact]
    public async Task Recovery_completes_a_job_whose_final_file_is_already_present()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-final/4", "recover-final.bin") with
        {
            State = JobState.Downloading,
            TotalBytes = 4,
            DoneBytes = 4,
        };
        await harness.Jobs.AddAsync(job, ct);
        await File.WriteAllBytesAsync(Path.Combine(job.Folder, job.FileName), [1, 2, 3, 4], ct);

        await harness.Engine.StartAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
    }

    [Fact]
    public async Task Recovery_requeues_a_job_that_was_still_probing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-probing/1048576", "recover-probing.bin") with { State = JobState.Probing };
        await harness.Jobs.AddAsync(job, ct);

        await harness.Engine.StartAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Queued);
    }

    [Fact]
    public async Task Start_refuses_a_database_that_fails_quick_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var failing = new FailingQuickCheckJobs(harness.Jobs);
        await using var engine = new DownloadEngine(failing, harness.Segments, harness.Writer, new BandwidthLimiter(), timeProvider: TimeProvider.System);

        Func<Task> start = () => engine.StartAsync(ct);

        await start.Should().ThrowAsync<InvalidOperationException>();
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

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string folder, Uri baseAddress, JobRepository jobs, SegmentRepository segments, CheckpointWriter writer, DownloadEngine engine)
        {
            Folder = folder;
            BaseAddress = baseAddress;
            Jobs = jobs;
            Segments = segments;
            Writer = writer;
            Engine = engine;
        }

        public string Folder { get; }

        public Uri BaseAddress { get; }

        public JobRepository Jobs { get; }

        public SegmentRepository Segments { get; }

        public CheckpointWriter Writer { get; }

        public DownloadEngine Engine { get; }

        public static async Task<Harness> CreateAsync(TestServerHost host, string root, bool startEngine = true)
        {
            var folder = Path.Combine(root, "downloads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var factory = new SqliteConnectionFactory(Path.Combine(folder, "engine.db"));
            await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
            var jobs = new JobRepository(factory);
            var segments = new SegmentRepository(factory);
#pragma warning disable CA2000 // Harness owns the writer and engine and disposes both
            var writer = new CheckpointWriter(factory);
            writer.Start();
            var engine = new DownloadEngine(jobs, segments, writer, new BandwidthLimiter(), timeProvider: TimeProvider.System);
#pragma warning restore CA2000
            if (startEngine)
            {
                await engine.StartAsync(TestContext.Current.CancellationToken);
            }

            _ = host;
            return new Harness(folder, host.BaseAddress, jobs, segments, writer, engine);
        }

        public Job NewJob(string path, string fileName) => new()
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = new Uri(BaseAddress, path).AbsoluteUri,
            FileName = fileName,
            Folder = Folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
            Connections = 4,
            TotalBytes = null,
        };

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Writer.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Folder))
                {
                    Directory.Delete(Folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // The test's root cleanup will retry once the pool has released the file.
            }
        }
    }

    private sealed class FailingQuickCheckJobs(IJobRepository inner) : IJobRepository
    {
        public Task AddAsync(Job job, CancellationToken cancellationToken = default) => inner.AddAsync(job, cancellationToken);

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(queueId, state, cancellationToken);

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default) => inner.UpdateAsync(job, cancellationToken);

        public Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default) =>
            inner.UpdateTransferMetadataAsync(id, metadata, cancellationToken);

        public Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default) =>
            inner.UpdateUrlAsync(id, url, finalUrl, cancellationToken);

        public Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default) => inner.ResetProgressAsync(id, cancellationToken);

        public Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);

        public Task<bool> TryChangeStateAsync(JobId id, JobState fromState, JobState toState, string? failureMessage = null, PauseReason? pauseReason = null, CancellationToken cancellationToken = default) =>
            inner.TryChangeStateAsync(id, fromState, toState, failureMessage, pauseReason, cancellationToken);

        public Task<bool> ReconcileProgressAsync(JobId id, long doneBytes, CancellationToken cancellationToken = default) =>
            inner.ReconcileProgressAsync(id, doneBytes, cancellationToken);

        public Task<bool> QuickCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
