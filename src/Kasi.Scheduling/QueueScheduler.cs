using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Periodically evaluates queue schedules against injected time, starting queues when windows open
/// and stopping them when windows close, with missed schedule catch-up on startup (TRD 10.2, KASI-QUE-003).
/// </summary>
public sealed class QueueScheduler : IDisposable
{
    private readonly IQueueRunner _runner;
    private readonly IQueueScheduleRepository _scheduleRepository;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, bool> _wasActive = [];
    private ITimer? _timer;

    public QueueScheduler(
        IQueueRunner runner,
        IQueueScheduleRepository scheduleRepository,
        TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _scheduleRepository = scheduleRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when a periodic evaluation throws; the timer keeps running.</summary>
    public event EventHandler<Exception>? EvaluationFailed;

    /// <summary>Starts periodic schedule evaluation and checks for currently active windows.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EvaluateAllSchedulesAsync(cancellationToken).ConfigureAwait(false);

        _timer = _timeProvider.CreateTimer(
            callback: _ => _ = EvaluateSafeAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));
    }

    /// <summary>Evaluates all enabled schedules at the current time and applies transitions.</summary>
    public async Task EvaluateAllSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var schedules = await _scheduleRepository.ListAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetLocalNow();

            foreach (var schedule in schedules)
            {
                if (!schedule.IsEnabled)
                {
                    continue;
                }

                bool active = ScheduleEvaluator.IsActive(schedule, now);
                bool isRunning = _runner.IsQueueRunning(schedule.QueueId);

                // Start only when the window opens (or on the first look), so a queue the user stopped by hand inside its window stays stopped.
                bool opened = active && (!_wasActive.TryGetValue(schedule.QueueId, out var was) || !was);
                _wasActive[schedule.QueueId] = active;

                if (opened && !isRunning)
                {
                    await _runner.StartQueueAsync(schedule.QueueId, cancellationToken).ConfigureAwait(false);
                }
                else if (!active && isRunning && schedule.StopTime is not null)
                {
                    await _runner.StopQueueAsync(schedule.QueueId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }

    private async Task EvaluateSafeAsync()
    {
        try
        {
            await EvaluateAllSchedulesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failing tick must not crash the process; report it and try again on the next tick.
            EvaluationFailed?.Invoke(this, ex);
        }
    }
}
