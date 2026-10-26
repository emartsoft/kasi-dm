using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.App.ViewModels;
using Kasi.Core;

namespace Kasi.App.Tests;

public sealed class SchedulerViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SchedulerViewModel_loads_queue_and_schedule_and_saves_updates()
    {
        var jobService = new FakeJobService();
        var model = new SchedulerViewModel(jobService);

        await model.LoadAsync(1, Ct);

        model.Queues.Should().NotBeEmpty();
        model.SelectedQueue?.Name.Should().Be("Main");
        model.MaxParallel.Should().Be(4);
        model.IsScheduleEnabled.Should().BeFalse();

        // Update settings
        model.MaxParallel = 8;
        model.IsScheduleEnabled = true;
        model.StartTimeText = "22:00";
        model.StopTimeText = "05:00";
        model.RunUntilEmpty = false;
        model.Sunday = false;
        model.SelectedPostAction = model.PostActions.First(p => p.Kind == PostActionKind.Sleep);
        model.ForcePostAction = true;

        bool closed = false;
        model.RequestClose += () => closed = true;

        await model.SaveCommand.ExecuteAsync(null);

        closed.Should().BeTrue();
        jobService.SavedQueue.Should().NotBeNull();
        jobService.SavedQueue!.MaxParallel.Should().Be(8);
        jobService.SavedQueue.PostAction.Should().Be("sleep:force");

        jobService.SavedSchedule.Should().NotBeNull();
        jobService.SavedSchedule!.IsEnabled.Should().BeTrue();
        jobService.SavedSchedule.StartTime.Should().Be(new TimeOnly(22, 0));
        jobService.SavedSchedule.StopTime.Should().Be(new TimeOnly(5, 0));
        (jobService.SavedSchedule.DaysOfWeek & DaysOfWeek.Sunday).Should().Be(DaysOfWeek.None);
        (jobService.SavedSchedule.DaysOfWeek & DaysOfWeek.Monday).Should().Be(DaysOfWeek.Monday);
    }

    [Fact]
    public async Task SchedulerViewModel_validates_bounds_and_invalid_time_strings()
    {
        var jobService = new FakeJobService();
        var model = new SchedulerViewModel(jobService);

        await model.LoadAsync(1, Ct);

        // Invalid max parallel
        model.MaxParallel = 0;
        await model.SaveCommand.ExecuteAsync(null);
        model.StatusText.Should().Contain("between 1 and 20");
        jobService.SavedQueue.Should().BeNull();

        // Invalid time format
        model.MaxParallel = 4;
        model.StartTimeText = "not-a-time";
        await model.SaveCommand.ExecuteAsync(null);
        model.StatusText.Should().Contain("Invalid start time");
        jobService.SavedQueue.Should().BeNull();
    }

    [Fact]
    public async Task SchedulerViewModel_selects_and_saves_a_speed_profile_for_the_window()
    {
        var jobService = new FakeJobService
        {
            Profiles = [new SpeedProfile { Id = 4, Name = "Night", LimitBps = 2_000_000, SortOrder = 0 }],
        };
        var model = new SchedulerViewModel(jobService);
        await model.LoadAsync(1, Ct);

        model.SpeedProfiles.Should().Contain(choice => choice.Id == 4 && choice.Label == "Night");
        model.SelectedScheduleProfile = model.SpeedProfiles.Single(choice => choice.Id == 4);
        await model.SaveCommand.ExecuteAsync(null);

        jobService.SavedSchedule!.ProfileId.Should().Be(4);
    }

    [Fact]
    public async Task SchedulerViewModel_saves_the_quota_and_shows_the_wake_timer_warning()
    {
        var jobService = new FakeJobService { WakeTimers = WakeTimerPolicy.Disabled };
        var model = new SchedulerViewModel(jobService);
        await model.LoadAsync(1, Ct);

        model.WakeTimerWarning.Should().BeTrue();
        model.QuotaEnabled = true;
        model.QuotaGigabytes = 2.5m;
        model.SelectedQuotaReset = model.QuotaResets.First(choice => choice.Reset == QuotaReset.Weekly);

        await model.SaveCommand.ExecuteAsync(null);

        jobService.SavedQueue.Should().NotBeNull();
        jobService.SavedQueue!.QuotaBytes.Should().Be(2_500_000_000);
        jobService.SavedQueue.QuotaReset.Should().Be(QuotaReset.Weekly);
    }

    [Fact]
    public async Task SchedulerViewModel_refuses_an_invalid_quota()
    {
        var jobService = new FakeJobService();
        var model = new SchedulerViewModel(jobService);
        await model.LoadAsync(1, Ct);

        model.QuotaEnabled = true;
        model.QuotaGigabytes = 0;
        await model.SaveCommand.ExecuteAsync(null);

        model.StatusText.Should().Be(Strings.SchedulerErrQuota);
        jobService.SavedQueue.Should().BeNull();
    }
    private sealed class FakeJobService : IJobService
    {
        public Core.Queue? SavedQueue { get; private set; }
        public WakeTimerPolicy WakeTimers { get; set; } = WakeTimerPolicy.Allowed;
        public IReadOnlyList<SpeedProfile> Profiles { get; set; } = [];
        public QueueSchedule? SavedSchedule { get; private set; }

#pragma warning disable CS0067
        public event Action<EngineEvent>? EventRaised;
#pragma warning restore CS0067

        public Task<IReadOnlyList<SpeedProfile>> SpeedProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Profiles);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Job>>([]);

        public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Category>>([]);

        public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Core.Queue>>([new Core.Queue { Id = 1, Name = "Main", MaxParallel = 4, SortOrder = 0 }]);

        public Task<AddResult> AddAsync(NewDownload request, Kasi.Engine.DuplicateAction? resolution = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);

        public Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public long? SpeedLimit { get; set; }

        public Task PauseAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<QueueSchedule?> GetScheduleAsync(int queueId, CancellationToken cancellationToken = default) =>
            Task.FromResult<QueueSchedule?>(null);

        public Task SaveScheduleAsync(QueueSchedule schedule, CancellationToken cancellationToken = default)
        {
            SavedSchedule = schedule;
            return Task.CompletedTask;
        }

        public Task SaveQueueAsync(Core.Queue queue, CancellationToken cancellationToken = default)
        {
            SavedQueue = queue;
            return Task.CompletedTask;
        }
    }
}
