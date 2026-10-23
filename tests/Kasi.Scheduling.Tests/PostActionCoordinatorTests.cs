using Kasi.Core;
using Kasi.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Scheduling.Tests;

public sealed class PostActionCoordinatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "KASI-QUE-004")]
    [Trait("TC", "TC-QUE-004")]
    public async Task TC_QUE_004_Power_action_runs_60_second_cancellable_countdown_and_executes_on_expiry()
    {
        var clock = new FakeTimeProvider();
        var powerController = new FakePowerController();
        var programRunner = new FakeProgramRunner();
        var queueRepo = new InMemoryQueueRepository();
        var jobRepo = new InMemoryJobRepository();
        using var runner = new TestQueueRunner();

        // Queue with shutdown post-action
        await queueRepo.AddAsync(new Queue { Id = 1, Name = "Main", PostAction = "shutdown", SortOrder = 0 }, Ct);

        using var coordinator = new PostActionCoordinator(runner, queueRepo, jobRepo, powerController, programRunner, clock);

        var countdownTicks = new List<int>();
        coordinator.CountdownTicked += (_, seconds) => countdownTicks.Add(seconds);

        // Trigger queue completion
        var actionTask = coordinator.ExecuteForQueueAsync(1, Ct);

        // Verify countdown started at 60
        coordinator.IsCountingDown.Should().BeTrue();
        coordinator.CountdownRemainingSeconds.Should().Be(60);

        // Advance 30 seconds
        clock.Advance(TimeSpan.FromSeconds(30));
        coordinator.CountdownRemainingSeconds.Should().Be(30);
        powerController.ShutdownCount.Should().Be(0);

        // Advance remaining 30 seconds -> expires
        clock.Advance(TimeSpan.FromSeconds(30));
        await actionTask;

        // Verify power action executed
        powerController.ShutdownCount.Should().Be(1);
        coordinator.IsCountingDown.Should().BeFalse();
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-004")]
    [Trait("TC", "TC-QUE-004")]
    public async Task TC_QUE_004_Countdown_cancellation_prevents_power_action_execution()
    {
        var clock = new FakeTimeProvider();
        var powerController = new FakePowerController();
        var programRunner = new FakeProgramRunner();
        var queueRepo = new InMemoryQueueRepository();
        var jobRepo = new InMemoryJobRepository();
        using var runner = new TestQueueRunner();

        await queueRepo.AddAsync(new Queue { Id = 1, Name = "Main", PostAction = "sleep", SortOrder = 0 }, Ct);

        using var coordinator = new PostActionCoordinator(runner, queueRepo, jobRepo, powerController, programRunner, clock);

        bool cancelledFired = false;
        coordinator.CountdownCancelled += (_, _) => cancelledFired = true;

        var actionTask = coordinator.ExecuteForQueueAsync(1, Ct);

        // Advance 10 seconds
        clock.Advance(TimeSpan.FromSeconds(10));
        coordinator.CountdownRemainingSeconds.Should().Be(50);

        // User clicks Cancel
        coordinator.CancelCountdown();
        cancelledFired.Should().BeTrue();
        coordinator.IsCountingDown.Should().BeFalse();

        // Advance past 60s
        clock.Advance(TimeSpan.FromSeconds(100));
        await actionTask;

        // Sleep should NOT be called
        powerController.SleepCount.Should().Be(0);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-004")]
    [Trait("TC", "TC-QUE-004")]
    public async Task TC_QUE_004_Multi_queue_busy_guard_skips_power_action_unless_forced()
    {
        var clock = new FakeTimeProvider();
        var powerController = new FakePowerController();
        var programRunner = new FakeProgramRunner();
        var queueRepo = new InMemoryQueueRepository();
        var jobRepo = new InMemoryJobRepository();
        using var runner = new TestQueueRunner();

        await queueRepo.AddAsync(new Queue { Id = 1, Name = "Queue1", PostAction = "hibernate", SortOrder = 0 }, Ct);
        await queueRepo.AddAsync(new Queue { Id = 2, Name = "Queue2", PostAction = "none", SortOrder = 1 }, Ct);

        // Queue 2 is currently running transfers
        await runner.StartQueueAsync(2, Ct);

        using var coordinator = new PostActionCoordinator(runner, queueRepo, jobRepo, powerController, programRunner, clock);

        bool skipped = false;
        coordinator.ActionSkippedDueToBusyQueues += (_, qid) =>
        {
            if (qid == 1)
            {
                skipped = true;
            }
        };

        // Queue 1 finishes
        await coordinator.ExecuteForQueueAsync(1, Ct);

        // Action was skipped!
        skipped.Should().BeTrue();
        coordinator.IsCountingDown.Should().BeFalse();
        powerController.HibernateCount.Should().Be(0);

        // Now test with forced override
        await queueRepo.UpdateAsync(new Queue { Id = 1, Name = "Queue1", PostAction = "hibernate:force", SortOrder = 0 }, Ct);

        var actionTask = coordinator.ExecuteForQueueAsync(1, Ct);
        coordinator.IsCountingDown.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(60));
        await actionTask;

        powerController.HibernateCount.Should().Be(1);
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-004")]
    [Trait("TC", "TC-QUE-004")]
    public async Task TC_QUE_004_Exit_and_RunProgram_actions_execute_as_expected()
    {
        var clock = new FakeTimeProvider();
        var powerController = new FakePowerController();
        var programRunner = new FakeProgramRunner();
        var queueRepo = new InMemoryQueueRepository();
        var jobRepo = new InMemoryJobRepository();
        using var runner = new TestQueueRunner();

        // 1. Exit Kasi action
        await queueRepo.AddAsync(new Queue { Id = 1, Name = "ExitQueue", PostAction = "exit", SortOrder = 0 }, Ct);

        using var coordinator = new PostActionCoordinator(runner, queueRepo, jobRepo, powerController, programRunner, clock);

        bool exitRequested = false;
        coordinator.ExitRequested += (_, _) => exitRequested = true;

        await coordinator.ExecuteForQueueAsync(1, Ct);
        exitRequested.Should().BeTrue();

        // 2. Run Program action with %1 parameter substitution
        await queueRepo.AddAsync(new Queue { Id = 2, Name = "RunQueue", PostAction = "run:C:\\tools\\process.exe --file %1", SortOrder = 1 }, Ct);

        var jobId = JobId.New(TimeProvider.System);
        await jobRepo.AddAsync(new Job
        {
            Id = jobId,
            Kind = JobKind.Http,
            Url = "http://example.com/video.mp4",
            FileName = "video.mp4",
            Folder = "C:\\Downloads\\Video",
            State = JobState.Completed,
            QueueId = 2,
            QueuePos = 1000.0,
            CreatedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
        }, Ct);

        await coordinator.ExecuteForQueueAsync(2, Ct);

        programRunner.ExecutedProgram.Should().Be("C:\\tools\\process.exe");
        programRunner.ExecutedArguments.Should().Be("--file %1");
        programRunner.ExecutedFilePath.Should().Be("C:\\Downloads\\Video\\video.mp4");
    }

    private sealed class FakePowerController : IPowerController
    {
        public int SleepCount { get; private set; }
        public int HibernateCount { get; private set; }
        public int ShutdownCount { get; private set; }

        public Task SleepAsync(CancellationToken cancellationToken = default)
        {
            SleepCount++;
            return Task.CompletedTask;
        }

        public Task HibernateAsync(CancellationToken cancellationToken = default)
        {
            HibernateCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProgramRunner : IProgramRunner
    {
        public string? ExecutedProgram { get; private set; }
        public string? ExecutedArguments { get; private set; }
        public string? ExecutedFilePath { get; private set; }

        public Task RunAsync(string programPath, string? arguments, string? completedFilePath = null, CancellationToken cancellationToken = default)
        {
            ExecutedProgram = programPath;
            ExecutedArguments = arguments;
            ExecutedFilePath = completedFilePath;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryQueueRepository : IQueueRepository
    {
        private readonly List<Queue> _queues = [];

        public Task<Queue?> GetAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_queues.FirstOrDefault(q => q.Id == id));

        public Task<IReadOnlyList<Queue>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Queue>>([.. _queues]);

        public Task<int> AddAsync(Queue queue, CancellationToken cancellationToken = default)
        {
            _queues.Add(queue);
            return Task.FromResult(queue.Id);
        }

        public Task<bool> UpdateAsync(Queue queue, CancellationToken cancellationToken = default)
        {
            int idx = _queues.FindIndex(q => q.Id == queue.Id);
            if (idx >= 0)
            {
                _queues[idx] = queue;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_queues.RemoveAll(q => q.Id == id) > 0);
    }

    private sealed class InMemoryJobRepository : IJobRepository
    {
        private readonly List<Job> _jobs = [];

        public Task AddAsync(Job job, CancellationToken cancellationToken = default)
        {
            _jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));

        public Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default)
        {
            IEnumerable<Job> query = _jobs;
            if (queueId is not null)
            {
                query = query.Where(j => j.QueueId == queueId.Value);
            }
            if (state is not null)
            {
                query = query.Where(j => j.State == state.Value);
            }
            return Task.FromResult<IReadOnlyList<Job>>([.. query]);
        }

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateQueuePositionAsync(JobId id, int queueId, double queuePos, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> TryChangeStateAsync(JobId id, JobState fromState, JobState toState, string? failureMessage = null, PauseReason? pauseReason = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ReconcileProgressAsync(JobId id, long doneBytes, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int?> IncrementRestartCountAsync(JobId id, CancellationToken cancellationToken = default) => Task.FromResult<int?>(0);

        public Task<bool> QuickCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestQueueRunner : IQueueRunner
    {
        private readonly HashSet<int> _running = [];

#pragma warning disable CS0067
        public event EventHandler<int>? QueueCompleted;
#pragma warning restore CS0067

        public bool IsQueueRunning(int queueId) => _running.Contains(queueId);

        public Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default)
        {
            _running.Add(queueId);
            return Task.CompletedTask;
        }

        public Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default)
        {
            _running.Remove(queueId);
            return Task.CompletedTask;
        }

        public Task PumpQueueAsync(int queueId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task OnJobStateChangedAsync(JobId jobId, JobState newState, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ReorderJobAsync(JobId jobId, JobId? beforeJobId, JobId? afterJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> MoveJobToQueueAsync(JobId jobId, int targetQueueId, JobId? beforeJobId = null, JobId? afterJobId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void Dispose() { }
    }
}
