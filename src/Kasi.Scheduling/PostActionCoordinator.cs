using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Executes post-completion actions when a queue becomes empty, enforcing the 60-second cancellable
/// countdown for power actions and multi-queue busy guards (TRD 10.3, KASI-QUE-004, TC-QUE-004).
/// </summary>
public sealed class PostActionCoordinator : IDisposable
{
    private readonly IQueueRunner _runner;
    private readonly IQueueRepository _queueRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IPowerController _powerController;
    private readonly IProgramRunner _programRunner;
    private readonly TimeProvider _timeProvider;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _countdownTimer;
    private CancellationTokenSource? _countdownCts;
    private int _remainingSeconds;

    public PostActionCoordinator(
        IQueueRunner runner,
        IQueueRepository queueRepository,
        IJobRepository jobRepository,
        IPowerController powerController,
        IProgramRunner programRunner,
        TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _queueRepository = queueRepository;
        _jobRepository = jobRepository;
        _powerController = powerController;
        _programRunner = programRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _runner.QueueCompleted += OnQueueCompleted;
    }

    public event EventHandler<int>? CountdownTicked;

    public event EventHandler? CountdownCancelled;

    public event EventHandler<PostActionKind>? ActionExecuting;

    public event EventHandler<int>? ActionSkippedDueToBusyQueues;

    public event EventHandler? ExitRequested;

    public bool IsCountingDown => _countdownCts is not null && !_countdownCts.IsCancellationRequested;

    public int CountdownRemainingSeconds => _remainingSeconds;

    public void CancelCountdown()
    {
        _countdownCts?.Cancel();
        _countdownTimer?.Dispose();
        _countdownTimer = null;
        CountdownCancelled?.Invoke(this, EventArgs.Empty);
    }

    public async Task ExecuteForQueueAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var queue = await _queueRepository.GetAsync(queueId, cancellationToken).ConfigureAwait(false);
        if (queue is null)
        {
            return;
        }

        var config = PostActionParser.Parse(queue.PostAction);
        if (config.Kind == PostActionKind.None)
        {
            return;
        }

        // Multi-queue busy guard (TRD 10.3): skip power actions if other queues are running unless forced
        if (!config.ForceIfOtherQueuesBusy)
        {
            var allQueues = await _queueRepository.ListAsync(cancellationToken).ConfigureAwait(false);
            bool anyOtherRunning = allQueues.Any(q => q.Id != queueId && _runner.IsQueueRunning(q.Id));
            if (anyOtherRunning)
            {
                ActionSkippedDueToBusyQueues?.Invoke(this, queueId);
                return;
            }
        }

        switch (config.Kind)
        {
            case PostActionKind.Sleep:
            case PostActionKind.Hibernate:
            case PostActionKind.Shutdown:
                await StartPowerCountdownAsync(config.Kind, cancellationToken).ConfigureAwait(false);
                break;

            case PostActionKind.ExitKasi:
                ActionExecuting?.Invoke(this, PostActionKind.ExitKasi);
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;

            case PostActionKind.RunProgram when !string.IsNullOrWhiteSpace(config.ProgramPath):
                ActionExecuting?.Invoke(this, PostActionKind.RunProgram);
                var jobs = await _jobRepository.ListAsync(queueId: queueId, cancellationToken: cancellationToken).ConfigureAwait(false);
                var lastCompleted = jobs.Where(j => j.State == JobState.Completed)
                                        .OrderByDescending(j => j.CompletedUtc)
                                        .FirstOrDefault();
                string? filePath = lastCompleted is not null
                    ? Path.Combine(lastCompleted.Folder, lastCompleted.FileName)
                    : null;
                await _programRunner.RunAsync(config.ProgramPath, config.Arguments, filePath, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public void Dispose()
    {
        _runner.QueueCompleted -= OnQueueCompleted;
        _countdownTimer?.Dispose();
        _countdownCts?.Dispose();
        _gate.Dispose();
    }

    private void OnQueueCompleted(object? sender, int queueId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteForQueueAsync(queueId).ConfigureAwait(false);
            }
            catch
            {
                // Background post-action execution errors should not crash the runner
            }
        });
    }

    private async Task StartPowerCountdownAsync(PostActionKind action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelCountdown();

            _countdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _remainingSeconds = 60;
            CountdownTicked?.Invoke(this, _remainingSeconds);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = _countdownCts.Token;
            token.Register(() => tcs.TrySetCanceled(token));

            _countdownTimer = _timeProvider.CreateTimer(
                callback: _ =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _remainingSeconds--;
                    CountdownTicked?.Invoke(this, _remainingSeconds);

                    if (_remainingSeconds <= 0)
                    {
                        _countdownTimer?.Dispose();
                        _countdownTimer = null;
                        tcs.TrySetResult();
                    }
                },
                state: null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromSeconds(1));

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!token.IsCancellationRequested)
            {
                ActionExecuting?.Invoke(this, action);
                switch (action)
                {
                    case PostActionKind.Sleep:
                        await _powerController.SleepAsync(token).ConfigureAwait(false);
                        break;
                    case PostActionKind.Hibernate:
                        await _powerController.HibernateAsync(token).ConfigureAwait(false);
                        break;
                    case PostActionKind.Shutdown:
                        await _powerController.ShutdownAsync(token).ConfigureAwait(false);
                        break;
                }
            }
            _countdownCts?.Dispose();
            _countdownCts = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
