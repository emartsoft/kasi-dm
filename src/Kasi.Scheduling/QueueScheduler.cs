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
    private readonly IQueueQuota? _quota;
    private readonly IWakeTimer? _wakeTimer;
    private readonly IWakeTimerPolicy? _wakePolicy;
    private readonly ISpeedProfileRepository? _profiles;
    private readonly ISpeedLimitTarget? _limit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, bool> _wasActive = [];
    private ITimer? _timer;

    public QueueScheduler(
        IQueueRunner runner,
        IQueueScheduleRepository scheduleRepository,
        TimeProvider? timeProvider = null,
        IQueueQuota? quota = null,
        IWakeTimer? wakeTimer = null,
        IWakeTimerPolicy? wakePolicy = null,
        ISpeedProfileRepository? profiles = null,
        ISpeedLimitTarget? limit = null)
    {
        _runner = runner;
        _scheduleRepository = scheduleRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quota = quota;
        _wakeTimer = wakeTimer;
        _wakePolicy = wakePolicy;
        _profiles = profiles;
        _limit = limit;
#pragma warning disable IDE0031 // an event add cannot use the null-conditional operator
        if (_wakeTimer is not null)
        {
            _wakeTimer.Fired += OnWakeTimerFired;
        }
#pragma warning restore IDE0031
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
                bool wasActiveBefore = _wasActive.TryGetValue(schedule.QueueId, out var previous) && previous;
                _wasActive[schedule.QueueId] = active;

                if (opened)
                {
                    await ApplyProfileAsync(schedule, cancellationToken).ConfigureAwait(false);
                }
                else if (!active && wasActiveBefore)
                {
                    await ClearProfileAsync(schedule, cancellationToken).ConfigureAwait(false);
                }

                if (opened && !isRunning)
                {
                    await _runner.StartQueueAsync(schedule.QueueId, cancellationToken).ConfigureAwait(false);
                }
                else if (!active && isRunning && schedule.StopTime is not null)
                {
                    await _runner.StopQueueAsync(schedule.QueueId, cancellationToken).ConfigureAwait(false);
                }

                // A queue quota paused resumes on the first tick of the new period, in queue order (KASI-QUE-007).
                if (_quota is not null && _wasActive.TryGetValue(schedule.QueueId, out var windowActive) && windowActive)
                {
                    await _runner.TryResumeAfterQuotaAsync(schedule.QueueId, cancellationToken).ConfigureAwait(false);
                }
            }

            RearmWake(schedules, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyProfileAsync(QueueSchedule schedule, CancellationToken cancellationToken)
    {
        if (_profiles is null || _limit is null || schedule.ProfileId is not { } profileId)
        {
            return;
        }

        var profile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return;
        }

        _limit.SpeedLimit = profile.LimitBps;
        await _profiles.SetActiveProfileIdAsync(profile.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearProfileAsync(QueueSchedule schedule, CancellationToken cancellationToken)
    {
        if (_profiles is null || _limit is null || schedule.ProfileId is not { } profileId)
        {
            return;
        }

        var active = await _profiles.GetActiveProfileIdAsync(cancellationToken).ConfigureAwait(false);
        if (active != profileId)
        {
            return;
        }

        _limit.SpeedLimit = null;
        await _profiles.SetActiveProfileIdAsync(null, cancellationToken).ConfigureAwait(false);
    }

    private void RearmWake(IReadOnlyList<QueueSchedule> schedules, DateTimeOffset now)
    {
        if (_wakeTimer is null)
        {
            return;
        }

        if (_wakePolicy?.Check() == WakeTimerPolicy.Disabled)
        {
            _wakeTimer.Disarm();
            return;
        }

        DateTimeOffset? next = null;
        foreach (var schedule in schedules)
        {
            if (ScheduleEvaluator.NextStart(schedule, now) is { } candidate && (next is null || candidate < next))
            {
                next = candidate;
            }
        }

        if (next is { } wakeAt)
        {
            _wakeTimer.Arm(wakeAt);
        }
        else
        {
            _wakeTimer.Disarm();
        }
    }

    private void OnWakeTimerFired(object? sender, EventArgs args) => _ = EvaluateSafeAsync();

    public void Dispose()
    {
        if (_wakeTimer is not null)
        {
            _wakeTimer.Fired -= OnWakeTimerFired;
            _wakeTimer.Dispose();
        }

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
