using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
[Trait("Requirement", "KASI-RES-002")]
[Trait("Requirement", "KASI-RES-003")]
public sealed class HttpTransferCheckpointTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TC", "TC-RES-002")]
    public async Task A_checkpointed_download_resumes_from_the_segment_rows_in_sqlite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-resume";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 });
        var options = Options(4, db) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, cts.Token);
        await EventuallyAsync(async () =>
        {
            var rows = await db.Segments.LoadAsync(db.JobId, ct);
            return rows.Sum(s => s.Written) > 0;
        }, ct);
        await cts.CancelAsync();

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
        var saved = await db.Segments.LoadAsync(db.JobId, ct);
        saved.Should().NotBeEmpty();
        saved.Sum(s => s.Written).Should().BeGreaterThan(0);
        var job = (await db.Jobs.GetAsync(db.JobId, ct))!;
        job.TotalBytes.Should().Be(size);
        job.ETag.Should().NotBeNull();
        job.FileName.Should().Be(failed.ResolvedFileName);
        var partPath = Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue();
        ((long)(await db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{db.JobId}';"))!)
            .Should().BeGreaterThanOrEqualTo(failed.BytesFlushed);

        var resumed = await new HttpTransferRunner().ResumeAsync(
            new Uri(job.FinalUrl!),
            _folder,
            job.FileName,
            job.TotalBytes!.Value,
            job.ETag,
            job.LastModified,
            saved,
            Options(4, db),
            ct);

        var completed = resumed.Should().BeOfType<TransferCompleted>().Subject;
        File.Exists(partPath).Should().BeFalse();
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task A_live_download_persists_its_split_structure_to_SQLite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-structure";
        const int size = 32 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 1024 * 1024 });
        var options = Options(4, db) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, cts.Token);
        await EventuallyAsync(async () => (await db.Segments.LoadAsync(db.JobId, ct)).Count > 1, ct);
        await cts.CancelAsync();
        (await download).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);

        var saved = await db.Segments.LoadAsync(db.JobId, ct);
        saved.Count.Should().BeGreaterThan(1);
        saved.Should().OnlyContain(s => s.Written >= 0);
    }

    [Fact]
    public async Task A_failed_final_flush_never_records_a_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-flush-fails";
        const int size = 8 * 1024 * 1024;
        var options = Options(4, db) with
        {
            FlushFaultInjector = () => new IOException("simulated flush failure"),
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            options,
            ct);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.DiskError);
        var saved = await db.Segments.LoadAsync(db.JobId, ct);
        saved.Should().OnlyContain(s => s.Written == 0, "a cursor may be stored only after FlushFileBuffers succeeds");
        ((long)(await db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{db.JobId}';"))!).Should().Be(0);
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension)).Should().BeTrue();
    }

    [Fact]
    [Trait("TC", "TC-RES-005")]
    public async Task A_changed_mutable_file_between_sessions_is_restarted_with_the_new_content()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string id = "mutable-checkpoint";
        const int size = 8 * 1024 * 1024;
        _host.Mutable.Change(id, size);
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 });
        var partialOptions = Options(4, db) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/mutable/{id}"), _folder, partialOptions, cts.Token);
        await EventuallyAsync(async () =>
        {
            var rows = await db.Segments.LoadAsync(db.JobId, ct);
            return rows.Sum(s => s.Written) > 0;
        }, ct);
        await cts.CancelAsync();

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
        var saved = await db.Segments.LoadAsync(db.JobId, ct);
        var partPath = Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue();

        _host.Mutable.Change(id);

        var changed = await new HttpTransferRunner().ResumeAsync(
            Url($"/mutable/{id}"),
            _folder,
            failed.ResolvedFileName!,
            size,
            failed.ETag,
            failed.LastModified,
            saved,
            Options(4, db),
            ct);

        changed.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        File.Exists(partPath).Should().BeTrue();

        var restarted = await new HttpTransferRunner().DownloadAsync(
            Url($"/mutable/{id}"),
            _folder,
            Options(4, db),
            ct);

        var completed = restarted.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct))
            .Should().Be(GeneratedContent.Sha256Hex($"{id}.v2", size));
    }

    [Fact]
    [Trait("TC", "TC-RES-006")]
    public async Task A_mid_session_content_change_fails_with_FileChanged_and_keeps_the_part_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string id = "mutable-mid";
        const int size = 16 * 1024 * 1024;
        _host.Mutable.Change(id, size);
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 });
        var options = Options(4, db) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        var download = new HttpTransferRunner().DownloadAsync(Url($"/mutable/{id}"), _folder, options, ct);
        await EventuallyAsync(async () =>
        {
            var rows = await db.Segments.LoadAsync(db.JobId, ct);
            return rows.Sum(s => s.Written) > 0;
        }, ct);
        _host.Mutable.Change(id);

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension)).Should().BeTrue();
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName!)).Should().BeFalse();
    }

    [Fact]
    public async Task A_database_hiccup_on_two_ticks_does_not_fail_the_download()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-hiccup";
        const int size = 6 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 1024 * 1024 });
        var options = Options(4, db) with
        {
            CheckpointWriter = new FlakyCheckpointWriter(db.Writer, failFirst: 2),
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task A_persistent_checkpoint_failure_stops_the_transfer_promptly_and_keeps_the_part_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-persistent";
        const int size = 32 * 1024 * 1024; // about eight seconds at 1 MiB/s on four connections if nothing stopped it
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 1024 * 1024 });
        var options = Options(4, db) with
        {
            CheckpointWriter = new FlakyCheckpointWriter(db.Writer, failFirst: int.MaxValue),
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, ct);
        timer.Stop();

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.CheckpointError);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the transfer must stop instead of downloading on without a durable record");
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension)).Should().BeTrue();
    }

    [Fact]
    public async Task A_part_file_flush_failure_on_a_tick_keeps_its_disk_full_classification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        const string seed = "checkpoint-tick-disk-full";
        const int size = 16 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 1024 * 1024 });
        var flushes = 0;
        var options = Options(4, db) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
            FlushFaultInjector = () => Interlocked.Increment(ref flushes) == 2
                ? new IOException("simulated disk full", unchecked((int)0x80070070))
                : null, // only the second flush, a periodic tick, fails; the final flush then succeeds
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.DiskNotEnoughSpace);
    }

    [Fact]
    public async Task Cancelling_during_the_initial_structure_save_is_reported_as_cancelled_not_as_a_checkpoint_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(4, db) with { SegmentRepository = new BlockingSegments(db.Segments, entered) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var download = new HttpTransferRunner().DownloadAsync(Url("/files/checkpoint-cancel-structure/4194304"), _folder, options, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await cts.CancelAsync();

        (await download).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
    }

    [Fact]
    public async Task Cancelling_while_the_probe_metadata_is_saved_is_reported_as_cancelled_not_as_a_checkpoint_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CheckpointDb.CreateAsync(_folder, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(4, db) with { JobRepository = new BlockingJobs(db.Jobs, entered) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var download = new HttpTransferRunner().DownloadAsync(Url("/files/checkpoint-cancel-metadata/4194304"), _folder, options, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await cts.CancelAsync();

        (await download).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static HttpTransferOptions Options(int connections, CheckpointDb db) => new()
    {
        SchedulerOptions = new SegmentSchedulerOptions
        {
            MaxConnections = connections,
            InitialConnections = connections,
        },
        CheckpointJobId = db.JobId,
        CheckpointWriter = db.Writer,
        SegmentRepository = db.Segments,
        JobRepository = db.Jobs,
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the checkpoint should appear within 10 s");
            await Task.Delay(20, cancellationToken);
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

    /// <summary>Blocks the structure write until the caller cancels, to cancel exactly while it is in flight.</summary>
    private sealed class BlockingSegments(ISegmentRepository inner, TaskCompletionSource entered) : ISegmentRepository
    {
        public Task<IReadOnlyList<Segment>> LoadAsync(JobId jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);

        public async Task ReplaceAsync(JobId jobId, IReadOnlyList<Segment> segments, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Blocks the probe-metadata write until the caller cancels, to cancel exactly while it is in flight.</summary>
    private sealed class BlockingJobs(IJobRepository inner, TaskCompletionSource entered) : IJobRepository
    {
        public Task AddAsync(Job job, CancellationToken cancellationToken = default) => inner.AddAsync(job, cancellationToken);

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(queueId, state, cancellationToken);

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default) => inner.UpdateAsync(job, cancellationToken);

        public Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default) =>
            inner.UpdateUrlAsync(id, url, finalUrl, cancellationToken);

        public async Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }

        public Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default) => inner.ResetProgressAsync(id, cancellationToken);

        public Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    }

    /// <summary>Fails the first commits to simulate a database that is briefly unavailable, then delegates.</summary>
    private sealed class FlakyCheckpointWriter(ICheckpointWriter inner, int failFirst) : ICheckpointWriter
    {
        private int _commits;

        public void Record(JobId jobId, int segmentIndex, long written) => inner.Record(jobId, segmentIndex, written);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _commits) <= failFirst
                ? Task.FromException(new InvalidOperationException("simulated database failure"))
                : inner.FlushAsync(cancellationToken);
    }

    private sealed class CheckpointDb : IAsyncDisposable
    {
        private CheckpointDb(SqliteConnectionFactory factory, JobId jobId, JobRepository jobs, SegmentRepository segments, CheckpointWriter writer)
        {
            Factory = factory;
            JobId = jobId;
            Jobs = jobs;
            Segments = segments;
            Writer = writer;
        }

        public SqliteConnectionFactory Factory { get; }

        public JobId JobId { get; }

        public JobRepository Jobs { get; }

        public SegmentRepository Segments { get; }

        public CheckpointWriter Writer { get; }

        public static async Task<CheckpointDb> CreateAsync(string folder, CancellationToken cancellationToken)
        {
            var factory = new SqliteConnectionFactory(Path.Combine(folder, "checkpoint.db"));
            await new MigrationRunner(factory).MigrateAsync(cancellationToken);
            var jobs = new JobRepository(factory);
            var job = new Job
            {
                Id = JobId.New(TimeProvider.System),
                Kind = JobKind.Http,
                Url = "http://example.invalid/file",
                FileName = "checkpoint.bin",
                Folder = folder,
                QueueId = 1,
                QueuePos = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            await jobs.AddAsync(job, cancellationToken);
            var segments = new SegmentRepository(factory);
#pragma warning disable CA2000 // CheckpointDb owns the writer and disposes it
            var writer = new CheckpointWriter(factory);
#pragma warning restore CA2000
            writer.Start();
            return new CheckpointDb(factory, job.Id, jobs, segments, writer);
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}
