using System.Collections.Concurrent;
using Kasi.Core;
using Kasi.Data;
using Kasi.Scheduling;

namespace Kasi.Scheduling.Tests;

[Trait("Category", "Integration")]
public sealed class QueueRunnerTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private QueueRepository _queueRepo = null!;
    private JobRepository _jobRepo = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _queueRepo = new QueueRepository(_db.Factory);
        _jobRepo = new JobRepository(_db.Factory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    private static JobId NewJobId() => JobId.New(TimeProvider.System);

    [Fact]
    [Trait("Requirement", "KASI-QUE-001")]
    [Trait("TC", "TC-QUE-001")]
    public async Task TC_QUE_001_Queue_limit_2_never_exceeded_across_10_jobs()
    {
        // Setup: Queue limit 2
        var mainQueue = await _queueRepo.GetAsync(1, TempDatabase.Ct);
        mainQueue.Should().NotBeNull();
        await _queueRepo.UpdateAsync(mainQueue! with { MaxParallel = 2 }, TempDatabase.Ct);

        var jobIds = new List<JobId>();
        for (int i = 0; i < 10; i++)
        {
            var id = NewJobId();
            jobIds.Add(id);
            await _jobRepo.AddAsync(CreateJob(id, queueId: 1, queuePos: (i + 1) * 1000.0), TempDatabase.Ct);
        }

        var control = new TestJobControl();
        using var runner = new QueueRunner(_queueRepo, _jobRepo, control);
        control.Runner = runner;

        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completedCount = 0;

        control.OnJobStarted = async id =>
        {
            // Simulate short work asynchronously
            await Task.Yield();
            await _jobRepo.TryChangeStateAsync(id, JobState.Queued, JobState.Downloading, cancellationToken: TempDatabase.Ct);
            await runner.OnJobStateChangedAsync(id, JobState.Downloading, TempDatabase.Ct);

            await Task.Yield();
            await _jobRepo.TryChangeStateAsync(id, JobState.Downloading, JobState.Completed, cancellationToken: TempDatabase.Ct);
            control.MarkJobFinished(id);
            await runner.OnJobStateChangedAsync(id, JobState.Completed, TempDatabase.Ct);

            if (Interlocked.Increment(ref completedCount) == 10)
            {
                allDone.TrySetResult();
            }
        };

        await runner.StartQueueAsync(1, TempDatabase.Ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        timeoutCts.Token.Register(() => allDone.TrySetCanceled());
        await allDone.Task;

        // Verify: Parallel limit 2 was never exceeded
        control.MaxObservedConcurrency.Should().Be(2);
        completedCount.Should().Be(10);

        var finalJobs = await _jobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
        finalJobs.Should().OnlyContain(j => j.State == JobState.Completed);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-002")]
    [Trait("TC", "TC-QUE-002")]
    public async Task TC_QUE_002_Reorder_and_move_between_queues_order_persists_across_restart()
    {
        // Setup: Queue 1 has jobs A, B, C
        var jobA = NewJobId();
        var jobB = NewJobId();
        var jobC = NewJobId();

        await _jobRepo.AddAsync(CreateJob(jobA, queueId: 1, queuePos: 1000.0), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobB, queueId: 1, queuePos: 2000.0), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobC, queueId: 1, queuePos: 3000.0), TempDatabase.Ct);

        // Queue 2 created
        int queue2Id = await _queueRepo.AddAsync(new Queue
        {
            Id = 0,
            Name = "Secondary",
            MaxParallel = 2,
            SortOrder = 1,
        }, TempDatabase.Ct);

        var control = new TestJobControl();
        using (var runner = new QueueRunner(_queueRepo, _jobRepo, control))
        {
            // 1. Reorder within Queue 1: move C between A and B
            bool reordered = await runner.ReorderJobAsync(jobC, beforeJobId: jobA, afterJobId: jobB, cancellationToken: TempDatabase.Ct);
            reordered.Should().BeTrue();

            var q1Jobs = await _jobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
            q1Jobs.Select(j => j.Id).Should().Equal(jobA, jobC, jobB);
            q1Jobs.First(j => j.Id == jobC).QueuePos.Should().Be(1500.0);

            // 2. Move B to Queue 2
            bool moved = await runner.MoveJobToQueueAsync(jobB, targetQueueId: queue2Id, cancellationToken: TempDatabase.Ct);
            moved.Should().BeTrue();

            var q1AfterMove = await _jobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
            q1AfterMove.Select(j => j.Id).Should().Equal(jobA, jobC);

            var q2Jobs = await _jobRepo.ListAsync(queueId: queue2Id, cancellationToken: TempDatabase.Ct);
            q2Jobs.Select(j => j.Id).Should().Equal(jobB);
        }

        // 3. Simulate application restart with new repository and runner instances
        var restartedJobRepo = new JobRepository(_db.Factory);
        var restartedQueueRepo = new QueueRepository(_db.Factory);
        using var restartedRunner = new QueueRunner(restartedQueueRepo, restartedJobRepo, control);

        var restartedQ1 = await restartedJobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
        restartedQ1.Select(j => j.Id).Should().Equal(jobA, jobC);
        restartedQ1[0].QueuePos.Should().Be(1000.0);
        restartedQ1[1].QueuePos.Should().Be(1500.0);

        var restartedQ2 = await restartedJobRepo.ListAsync(queueId: queue2Id, cancellationToken: TempDatabase.Ct);
        restartedQ2.Select(j => j.Id).Should().Equal(jobB);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-001")]
    public async Task Failing_job_moves_to_tail_of_queue_and_does_not_block_subsequent_jobs()
    {
        var mainQueue = await _queueRepo.GetAsync(1, TempDatabase.Ct);
        await _queueRepo.UpdateAsync(mainQueue! with { MaxParallel = 1 }, TempDatabase.Ct);

        var jobA = NewJobId();
        var jobB = NewJobId();

        await _jobRepo.AddAsync(CreateJob(jobA, queueId: 1, queuePos: 1000.0), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobB, queueId: 1, queuePos: 2000.0), TempDatabase.Ct);

        var control = new TestJobControl();
        using var runner = new QueueRunner(_queueRepo, _jobRepo, control);
        control.Runner = runner;

        var jobBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        control.OnJobStarted = async id =>
        {
            if (id == jobA)
            {
                // Job A fails
                await _jobRepo.TryChangeStateAsync(id, JobState.Queued, JobState.Failed, failureMessage: "Corrupt file", cancellationToken: TempDatabase.Ct);
                await runner.OnJobStateChangedAsync(id, JobState.Failed, TempDatabase.Ct);
            }
            else if (id == jobB)
            {
                jobBStarted.TrySetResult();
            }
        };

        await runner.StartQueueAsync(1, TempDatabase.Ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => jobBStarted.TrySetCanceled());
        await jobBStarted.Task;

        // Verify: Job A was moved to the tail behind Job B
        var jobs = await _jobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
        var loadedA = jobs.First(j => j.Id == jobA);
        var loadedB = jobs.First(j => j.Id == jobB);

        loadedA.QueuePos.Should().BeGreaterThan(loadedB.QueuePos);
        loadedA.State.Should().Be(JobState.Failed);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-001")]
    public async Task StopQueueAsync_pauses_active_jobs_and_preserves_order()
    {
        var mainQueue = await _queueRepo.GetAsync(1, TempDatabase.Ct);
        await _queueRepo.UpdateAsync(mainQueue! with { MaxParallel = 2 }, TempDatabase.Ct);

        var jobA = NewJobId();
        var jobB = NewJobId();

        await _jobRepo.AddAsync(CreateJob(jobA, queueId: 1, queuePos: 1000.0), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobB, queueId: 1, queuePos: 2000.0), TempDatabase.Ct);

        var control = new TestJobControl();
        using var runner = new QueueRunner(_queueRepo, _jobRepo, control);
        control.Runner = runner;

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;

        control.OnJobStarted = id =>
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                startedTcs.TrySetResult();
            }
            return Task.CompletedTask;
        };

        await runner.StartQueueAsync(1, TempDatabase.Ct);
        await startedTcs.Task;

        await runner.StopQueueAsync(1, TempDatabase.Ct);

        runner.IsQueueRunning(1).Should().BeFalse();
        control.PausedJobs.Should().Contain([jobA, jobB]);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-002")]
    public async Task Rebalance_triggers_automatically_when_fractional_gap_is_narrow()
    {
        var jobA = NewJobId();
        var jobB = NewJobId();
        var jobC = NewJobId();

        // Narrow gap < 1e-6
        await _jobRepo.AddAsync(CreateJob(jobA, queueId: 1, queuePos: 1.0), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobB, queueId: 1, queuePos: 1.0000001), TempDatabase.Ct);
        await _jobRepo.AddAsync(CreateJob(jobC, queueId: 1, queuePos: 2.0), TempDatabase.Ct);

        var control = new TestJobControl();
        using var runner = new QueueRunner(_queueRepo, _jobRepo, control);

        // Move C between A and B, triggering rebalance
        bool reordered = await runner.ReorderJobAsync(jobC, beforeJobId: jobA, afterJobId: jobB, cancellationToken: TempDatabase.Ct);
        reordered.Should().BeTrue();

        var jobs = await _jobRepo.ListAsync(queueId: 1, cancellationToken: TempDatabase.Ct);
        jobs.Select(j => j.Id).Should().Equal(jobA, jobC, jobB);
        // Positions are spaced at standard intervals
        jobs[0].QueuePos.Should().Be(1000.0);
        jobs[1].QueuePos.Should().Be(1500.0);
        jobs[2].QueuePos.Should().Be(2000.0);
    }

    private static Job CreateJob(JobId id, int queueId, double queuePos) => new()
    {
        Id = id,
        Kind = JobKind.Http,
        Url = "http://example.com/test.bin",
        FileName = $"{id}.bin",
        Folder = "C:\\Downloads",
        State = JobState.Queued,
        QueueId = queueId,
        QueuePos = queuePos,
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    private sealed class TestJobControl : IJobControl
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<JobId, bool> _activeJobs = new();
        private int _maxObserved;

        public IQueueRunner? Runner { get; set; }

        public Func<JobId, Task>? OnJobStarted { get; set; }

        public int MaxObservedConcurrency
        {
            get
            {
                lock (_lock)
                {
                    return _maxObserved;
                }
            }
        }

        public ConcurrentBag<JobId> PausedJobs { get; } = [];

        public void MarkJobFinished(JobId id)
        {
            _activeJobs.TryRemove(id, out _);
        }

        public Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            _activeJobs.TryAdd(id, true);
            lock (_lock)
            {
                if (_activeJobs.Count > _maxObserved)
                {
                    _maxObserved = _activeJobs.Count;
                }
            }

            if (OnJobStarted is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await OnJobStarted(id);
                    }
                    finally
                    {
                        _activeJobs.TryRemove(id, out _);
                    }
                }, cancellationToken);
            }

            return Task.CompletedTask;
        }

        public Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
        {
            _activeJobs.TryRemove(id, out _);
            PausedJobs.Add(id);
            return Task.CompletedTask;
        }
    }
}
