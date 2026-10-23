using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.Core;

namespace Kasi.App.ViewModels;

internal sealed record PostActionChoice(PostActionKind Kind, string Label);

internal sealed partial class SchedulerViewModel : ObservableObject
{
    private readonly IJobService _jobService;
    private Core.Queue? _currentQueue;

    [ObservableProperty]
    private ObservableCollection<Core.Queue> _queues = [];

    [ObservableProperty]
    private Core.Queue? _selectedQueue;

    [ObservableProperty]
    private int _maxParallel = 4;

    [ObservableProperty]
    private bool _isScheduleEnabled;

    [ObservableProperty]
    private string _startTimeText = "23:00";

    [ObservableProperty]
    private string _stopTimeText = "06:00";

    [ObservableProperty]
    private bool _runUntilEmpty;

    [ObservableProperty]
    private bool _sunday = true;

    [ObservableProperty]
    private bool _monday = true;

    [ObservableProperty]
    private bool _tuesday = true;

    [ObservableProperty]
    private bool _wednesday = true;

    [ObservableProperty]
    private bool _thursday = true;

    [ObservableProperty]
    private bool _friday = true;

    [ObservableProperty]
    private bool _saturday = true;

    [ObservableProperty]
    private PostActionChoice _selectedPostAction;

    [ObservableProperty]
    private bool _forcePostAction;

    [ObservableProperty]
    private string _programPath = string.Empty;

    [ObservableProperty]
    private string _programArguments = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public IReadOnlyList<PostActionChoice> PostActions { get; } =
    [
        new(PostActionKind.None, "None"),
        new(PostActionKind.Sleep, "Sleep"),
        new(PostActionKind.Hibernate, "Hibernate"),
        new(PostActionKind.Shutdown, "Shut down"),
        new(PostActionKind.ExitKasi, "Exit Kasi"),
        new(PostActionKind.RunProgram, "Run program"),
    ];

    public event Action? RequestClose;

    public SchedulerViewModel(IJobService jobService, int? initialQueueId = null)
    {
        _jobService = jobService;
        _selectedPostAction = PostActions[0];
    }

    public async Task LoadAsync(int? selectQueueId = null, CancellationToken cancellationToken = default)
    {
        var list = await _jobService.QueuesAsync(cancellationToken).ConfigureAwait(true);
        Queues = new ObservableCollection<Core.Queue>(list);
        SelectedQueue = list.FirstOrDefault(q => q.Id == (selectQueueId ?? 1)) ?? (list.Count > 0 ? list[0] : null);
    }

    partial void OnSelectedQueueChanged(Core.Queue? value)
    {
        if (value is null)
        {
            return;
        }

        _ = LoadQueueDetailsAsync(value);
    }

    private async Task LoadQueueDetailsAsync(Core.Queue queue)
    {
        _currentQueue = queue;
        MaxParallel = queue.MaxParallel;

        var config = PostActionParser.Parse(queue.PostAction);
        SelectedPostAction = PostActions.FirstOrDefault(p => p.Kind == config.Kind) ?? PostActions[0];
        ForcePostAction = config.ForceIfOtherQueuesBusy;
        ProgramPath = config.ProgramPath ?? string.Empty;
        ProgramArguments = config.Arguments ?? string.Empty;

        var schedule = await _jobService.GetScheduleAsync(queue.Id).ConfigureAwait(true);
        if (schedule is not null)
        {
            IsScheduleEnabled = schedule.IsEnabled;
            StartTimeText = schedule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (schedule.StopTime is { } stop)
            {
                StopTimeText = stop.ToString("HH:mm", CultureInfo.InvariantCulture);
                RunUntilEmpty = false;
            }
            else
            {
                RunUntilEmpty = true;
            }

            Sunday = (schedule.DaysOfWeek & DaysOfWeek.Sunday) != 0;
            Monday = (schedule.DaysOfWeek & DaysOfWeek.Monday) != 0;
            Tuesday = (schedule.DaysOfWeek & DaysOfWeek.Tuesday) != 0;
            Wednesday = (schedule.DaysOfWeek & DaysOfWeek.Wednesday) != 0;
            Thursday = (schedule.DaysOfWeek & DaysOfWeek.Thursday) != 0;
            Friday = (schedule.DaysOfWeek & DaysOfWeek.Friday) != 0;
            Saturday = (schedule.DaysOfWeek & DaysOfWeek.Saturday) != 0;
        }
        else
        {
            IsScheduleEnabled = false;
            StartTimeText = "23:00";
            StopTimeText = "06:00";
            RunUntilEmpty = false;
            Sunday = Monday = Tuesday = Wednesday = Thursday = Friday = Saturday = true;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_currentQueue is null)
        {
            return;
        }

        if (MaxParallel < 1 || MaxParallel > 20)
        {
            StatusText = "Max parallel must be between 1 and 20.";
            return;
        }

        if (!TimeOnly.TryParse(StartTimeText, CultureInfo.InvariantCulture, out var startTime))
        {
            StatusText = "Invalid start time format (use HH:mm).";
            return;
        }

        TimeOnly? stopTime = null;
        if (!RunUntilEmpty)
        {
            if (!TimeOnly.TryParse(StopTimeText, CultureInfo.InvariantCulture, out var parsedStop))
            {
                StatusText = "Invalid stop time format (use HH:mm).";
                return;
            }
            stopTime = parsedStop;
        }

        DaysOfWeek days = DaysOfWeek.None;
        if (Sunday)
        {
            days |= DaysOfWeek.Sunday;
        }
        if (Monday)
        {
            days |= DaysOfWeek.Monday;
        }
        if (Tuesday)
        {
            days |= DaysOfWeek.Tuesday;
        }
        if (Wednesday)
        {
            days |= DaysOfWeek.Wednesday;
        }
        if (Thursday)
        {
            days |= DaysOfWeek.Thursday;
        }
        if (Friday)
        {
            days |= DaysOfWeek.Friday;
        }
        if (Saturday)
        {
            days |= DaysOfWeek.Saturday;
        }

        var postActionConfig = new PostActionConfig
        {
            Kind = SelectedPostAction.Kind,
            ForceIfOtherQueuesBusy = ForcePostAction,
            ProgramPath = SelectedPostAction.Kind == PostActionKind.RunProgram ? ProgramPath : null,
            Arguments = SelectedPostAction.Kind == PostActionKind.RunProgram ? ProgramArguments : null,
        };

        var updatedQueue = _currentQueue with
        {
            MaxParallel = MaxParallel,
            PostAction = PostActionParser.Format(postActionConfig),
        };

        var updatedSchedule = new QueueSchedule
        {
            QueueId = _currentQueue.Id,
            IsEnabled = IsScheduleEnabled,
            StartTime = startTime,
            StopTime = stopTime,
            DaysOfWeek = days,
        };

        await _jobService.SaveQueueAsync(updatedQueue).ConfigureAwait(true);
        await _jobService.SaveScheduleAsync(updatedSchedule).ConfigureAwait(true);

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
