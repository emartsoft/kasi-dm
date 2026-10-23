using Kasi.Core;
using Kasi.Data;
using Kasi.Scheduling;

namespace Kasi.Scheduling.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-QUE-004")]
public sealed class QueueRunnerCompletionTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private QueueRepository _queues = null!;
    private JobRepository _jobs = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _queues = new QueueRepository(_db.Factory);
        _jobs = new JobRepository(_db.Factory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    private sealed class NoControl : IJobControl
    {
        public Task StartJobAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Job Make(JobState state, double pos) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = "http://example.com/x.bin",
        FileName = "x" + pos + ".bin",
        Folder = "C:/Downloads",
        State = state,
        QueueId = 1,
        QueuePos = pos,
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_queue_holding_only_finished_jobs_does_not_report_completion_when_started()
    {
        await _jobs.AddAsync(Make(JobState.Completed, 1000), TempDatabase.Ct);
        using var runner = new QueueRunner(_queues, _jobs, new NoControl());
        var raised = 0;
        runner.QueueCompleted += (_, _) => raised++;

        await runner.StartQueueAsync(1, TempDatabase.Ct);
        raised.Should().Be(0, "nothing ran, so nothing completed");
    }

    [Fact]
    public async Task The_queue_stops_running_once_its_last_job_finishes()
    {
        var job = Make(JobState.Queued, 1000);
        await _jobs.AddAsync(job, TempDatabase.Ct);
        using var runner = new QueueRunner(_queues, _jobs, new NoControl());
        var raised = 0;
        runner.QueueCompleted += (_, _) => raised++;
        await runner.StartQueueAsync(1, TempDatabase.Ct);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Queued, JobState.Downloading, cancellationToken: TempDatabase.Ct);
        await runner.OnJobStateChangedAsync(job.Id, JobState.Downloading, TempDatabase.Ct);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Downloading, JobState.Verifying, cancellationToken: TempDatabase.Ct);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Verifying, JobState.Completed, cancellationToken: TempDatabase.Ct);

        await runner.OnJobStateChangedAsync(job.Id, JobState.Completed, TempDatabase.Ct);
        raised.Should().Be(1);
        runner.IsQueueRunning(1).Should().BeFalse("the queue finished");
    }

    [Fact]
    public async Task Pausing_the_only_job_of_a_running_queue_is_not_a_completed_queue()
    {
        var job = Make(JobState.Queued, 1000);
        await _jobs.AddAsync(job, TempDatabase.Ct);
        using var runner = new QueueRunner(_queues, _jobs, new NoControl());
        var raised = 0;
        runner.QueueCompleted += (_, _) => raised++;
        await runner.StartQueueAsync(1, TempDatabase.Ct);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Queued, JobState.Downloading, cancellationToken: TempDatabase.Ct);
        await runner.OnJobStateChangedAsync(job.Id, JobState.Downloading, TempDatabase.Ct);
        await _jobs.TryChangeStateAsync(job.Id, JobState.Downloading, JobState.Paused, pauseReason: PauseReason.UserRequested, cancellationToken: TempDatabase.Ct);

        await runner.OnJobStateChangedAsync(job.Id, JobState.Paused, TempDatabase.Ct);
        raised.Should().Be(0, "a paused download is not a finished queue");
    }
}
