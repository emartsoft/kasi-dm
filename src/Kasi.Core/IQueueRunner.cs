namespace Kasi.Core;

/// <summary>Coordinates concurrent execution of queued jobs and queue membership (TRD 10.1, KASI-QUE-001, KASI-QUE-002).</summary>
public interface IQueueRunner : IDisposable
{
    /// <summary>Raised when a queue finishes its active and queued jobs.</summary>
    event EventHandler<int>? QueueCompleted;

    /// <summary>Starts running queued jobs in the specified queue.</summary>
    Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default);

    /// <summary>Stops running jobs in the specified queue, pausing all active jobs.</summary>
    Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default);

    /// <summary>Checks whether the specified queue is currently active/running.</summary>
    bool IsQueueRunning(int queueId);

    /// <summary>Restarts a queue paused by quota enforcement once its period has capacity. A runner without quota accounting returns false.</summary>
    Task<bool> TryResumeAfterQuotaAsync(int queueId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Pumps open concurrency slots in the specified queue, starting waiting queued jobs up to <see cref="Queue.MaxParallel"/>.</summary>
    Task PumpQueueAsync(int queueId, CancellationToken cancellationToken = default);

    /// <summary>Notifies the runner that a job's state has changed, freeing a concurrency slot or shifting failed jobs to the queue tail.</summary>
    Task OnJobStateChangedAsync(JobId jobId, JobState newState, CancellationToken cancellationToken = default);

    /// <summary>Reorders a job within its current queue relative to adjacent jobs using fractional indexing.</summary>
    Task<bool> ReorderJobAsync(JobId jobId, JobId? beforeJobId, JobId? afterJobId, CancellationToken cancellationToken = default);

    /// <summary>Moves a job to a target queue, optionally positioned relative to adjacent jobs in that queue.</summary>
    Task<bool> MoveJobToQueueAsync(JobId jobId, int targetQueueId, JobId? beforeJobId = null, JobId? afterJobId = null, CancellationToken cancellationToken = default);
}
