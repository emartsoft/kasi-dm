using Kasi.Core;
using Kasi.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Scheduling.Tests;

public sealed class QueueSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "KASI-QUE-003")]
    [Trait("TC", "TC-QUE-003")]
    public async Task TC_QUE_003_Schedule_23_00_to_06_00_starts_and_stops_within_1_minute_with_injected_clock()
    {
        // Setup clock at 22:59:00 on Friday Oct 23, 2026
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 22, 59, 0, TimeSpan.Zero));

        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            StopTime = new TimeOnly(6, 0),
            DaysOfWeek = DaysOfWeek.All,
        };

        var repo = new InMemoryScheduleRepository([schedule]);
        using var runner = new TestQueueRunner();
        using var scheduler = new QueueScheduler(runner, repo, clock);

        await scheduler.StartAsync(Ct);

        // 22:59 -> queue not started yet
        runner.IsQueueRunning(1).Should().BeFalse();

        // Advance 1 minute to 23:00 -> timer fires, window opens
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.EvaluateAllSchedulesAsync(Ct); // Ensure evaluation completes
        runner.IsQueueRunning(1).Should().BeTrue();

        // Advance to 05:59 -> still running
        clock.Advance(TimeSpan.FromHours(7) - TimeSpan.FromMinutes(1));
        await scheduler.EvaluateAllSchedulesAsync(Ct);
        runner.IsQueueRunning(1).Should().BeTrue();

        // Advance 1 minute to 06:00 -> window closes, queue stops
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.EvaluateAllSchedulesAsync(Ct);
        runner.IsQueueRunning(1).Should().BeFalse();
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-003")]
    [Trait("TC", "TC-QUE-007")]
    public async Task TC_QUE_007_Missed_schedule_within_window_starts_at_once_outside_window_waits()
    {
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            StopTime = new TimeOnly(6, 0),
            DaysOfWeek = DaysOfWeek.All,
        };

        // Scenario A: PC comes on at 02:00 within the window
        var insideClock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero));
        var insideRepo = new InMemoryScheduleRepository([schedule]);
        using var insideRunner = new TestQueueRunner();
        using (var insideScheduler = new QueueScheduler(insideRunner, insideRepo, insideClock))
        {
            await insideScheduler.StartAsync(Ct);

            // Starts at once!
            insideRunner.IsQueueRunning(1).Should().BeTrue();
        }

        // Scenario B: PC comes on at 14:00 outside the window
        var outsideClock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero));
        var outsideRepo = new InMemoryScheduleRepository([schedule]);
        using var outsideRunner = new TestQueueRunner();
        using (var outsideScheduler = new QueueScheduler(outsideRunner, outsideRepo, outsideClock))
        {
            await outsideScheduler.StartAsync(Ct);

            // Remains stopped
            outsideRunner.IsQueueRunning(1).Should().BeFalse();

            // Waits until 23:00
            outsideClock.Advance(TimeSpan.FromHours(9)); // 23:00
            await outsideScheduler.EvaluateAllSchedulesAsync(Ct);
            outsideRunner.IsQueueRunning(1).Should().BeTrue();
        }
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-003")]
    public async Task A_queue_the_user_stopped_inside_its_window_stays_stopped_until_the_next_window()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero));
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            StopTime = new TimeOnly(6, 0),
            DaysOfWeek = DaysOfWeek.All,
        };
        using var runner = new TestQueueRunner();
        using var scheduler = new QueueScheduler(runner, new InMemoryScheduleRepository([schedule]), clock);
        await scheduler.StartAsync(Ct);
        runner.IsQueueRunning(1).Should().BeTrue();

        await runner.StopQueueAsync(1, Ct);
        clock.Advance(TimeSpan.FromMinutes(30));
        await scheduler.EvaluateAllSchedulesAsync(Ct);

        runner.IsQueueRunning(1).Should().BeFalse("the user stopped it; the window is still open but the scheduler must not fight that");

        clock.Advance(TimeSpan.FromHours(4)); // 06:30, window closed
        await scheduler.EvaluateAllSchedulesAsync(Ct);
        clock.Advance(TimeSpan.FromHours(17)); // 23:30, next window
        await scheduler.EvaluateAllSchedulesAsync(Ct);

        runner.IsQueueRunning(1).Should().BeTrue();
    }

    [Fact]
    [Trait("Requirement", "KASI-QUE-005")]
    [Trait("TC", "TC-QUE-005")]
    public async Task The_next_schedule_start_arms_the_wake_timer_and_a_disabled_policy_disarms_it()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var schedule = new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            StopTime = new TimeOnly(6, 0),
            DaysOfWeek = DaysOfWeek.All,
        };
        using var timer = new FakeWakeTimer();
        using var allowedRunner = new TestQueueRunner();
        using (var allowed = new QueueScheduler(
            allowedRunner,
            new InMemoryScheduleRepository([schedule]),
            clock,
            wakeTimer: timer,
            wakePolicy: new FixedPolicy(WakeTimerPolicy.Allowed)))
        {
            await allowed.StartAsync(Ct);
            timer.ArmedFor.Should().Be(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero));

            clock.Advance(TimeSpan.FromHours(11));
            await allowed.EvaluateAllSchedulesAsync(Ct);
            timer.ArmedFor.Should().Be(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero));
        }

        using var deniedRunner = new TestQueueRunner();
        using var denied = new QueueScheduler(
            deniedRunner,
            new InMemoryScheduleRepository([schedule]),
            clock,
            wakeTimer: timer,
            wakePolicy: new FixedPolicy(WakeTimerPolicy.Disabled));
        await denied.EvaluateAllSchedulesAsync(Ct);
        timer.ArmedFor.Should().BeNull();
    }

    private sealed class FixedPolicy(WakeTimerPolicy policy) : IWakeTimerPolicy
    {
        public WakeTimerPolicy Check() => policy;
    }

    private sealed class FakeWakeTimer : IWakeTimer
    {
        public DateTimeOffset? ArmedFor { get; private set; }

#pragma warning disable CS0067
        public event EventHandler? Fired;
#pragma warning restore CS0067

        public void Arm(DateTimeOffset wakeAtUtc) => ArmedFor = wakeAtUtc;

        public void Disarm() => ArmedFor = null;

        public void Dispose() => ArmedFor = null;
    }

    private sealed class InMemoryScheduleRepository(IEnumerable<QueueSchedule> initial) : IQueueScheduleRepository
    {
        private readonly List<QueueSchedule> _items = [.. initial];

        public Task<QueueSchedule?> GetAsync(int queueId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.FirstOrDefault(s => s.QueueId == queueId));

        public Task<IReadOnlyList<QueueSchedule>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QueueSchedule>>([.. _items]);

        public Task UpsertAsync(QueueSchedule schedule, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(s => s.QueueId == schedule.QueueId);
            _items.Add(schedule);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(int queueId, CancellationToken cancellationToken = default)
        {
            int removed = _items.RemoveAll(s => s.QueueId == queueId);
            return Task.FromResult(removed > 0);
        }
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
