using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Resources;
using Kasi.App.Startup;
using Kasi.App.Tray;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>Asks the user whether to delete; a real window in the app, a fake in tests.</summary>
internal interface IConfirmHost
{
    /// <summary>The choice, or null when the user cancelled.</summary>
    Task<DeleteChoice?> ConfirmDeleteAsync(ConfirmDeleteViewModel dialog);
}

/// <summary>
/// The main window (KASI-UI-001, wireframe): toolbar, sidebar, job list and status bar, and what each button does to the selected jobs.
/// Everything the engine is asked goes through <see cref="IJobService"/>, off the UI thread's critical path and never synchronously.
/// </summary>
internal sealed partial class MainWindowViewModel : ObservableObject, IAddDownloadService, IBrowserRequestSink, IDisposable
{
    private static readonly CompositeFormat _countsFormat = CompositeFormat.Parse(Strings.StatusCounts);
    private static readonly CompositeFormat _limitFormat = CompositeFormat.Parse(Strings.StatusLimit);
    private static readonly CompositeFormat _toolbarLimitFormat = CompositeFormat.Parse(Strings.ToolbarSpeedLimit);
    private static readonly CompositeFormat _deleteOneFormat = CompositeFormat.Parse(Strings.DeleteMessageOne);
    private static readonly CompositeFormat _deleteManyFormat = CompositeFormat.Parse(Strings.DeleteMessageMany);

    private readonly IJobService _service;
    private readonly IUiSettings _ui;
    private readonly IConfirmHost _confirm;
    private readonly IShell _shell;
    private readonly SpeedLimitState _limit;
    private IReadOnlyList<JobRowViewModel> _selection = [];

    /// <summary>The row the user has selected in the list (the first, when several are).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshLinkCommand))]
    private JobRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _extensionStatusText = Strings.StatusExtensionNone;

    [ObservableProperty]
    private string _speedLimitText = string.Empty;

    public MainWindowViewModel(
        JobListViewModel jobs,
        IJobService service,
        SidebarViewModel sidebar,
        IUiSettings ui,
        IConfirmHost confirm,
        IShell shell,
        SpeedLimitState limit,
        ColumnLayoutViewModel layout)
    {
        Layout = layout;
        Jobs = jobs;
        Sidebar = sidebar;
        _service = service;
        _ui = ui;
        _confirm = confirm;
        _shell = shell;
        _limit = limit;
        SpeedPresets = TrayIconViewModel.BuildPresets();
        _limit.Changed += OnLimitChanged;
        jobs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(JobListViewModel.ActiveCount) or nameof(JobListViewModel.QueuedCount))
            {
                OnPropertyChanged(nameof(CountsText));
            }
        };
        UpdateLimitText();
    }

    public JobListViewModel Jobs { get; }

    public SidebarViewModel Sidebar { get; }

    /// <summary>The grid's column layout and sort, restored when the window opens and saved as the user changes them.</summary>
    public ColumnLayoutViewModel Layout { get; }

    public IReadOnlyList<SpeedPreset> SpeedPresets { get; }

    /// <summary>Set by the app once the dialogs exist: asks for a fresh link for a job.</summary>
    public Action<JobId>? RefreshLinkHandler { get; set; }

    /// <summary>Set by the app once the dialogs exist: opens an empty Add download window.</summary>
    public Action? AddUrlHandler { get; set; }

    /// <summary>Set by the app once the settings window exists.</summary>
    public Action? SettingsHandler { get; set; }

    /// <summary>Set by the app: opens the Scheduler window (M2.10).</summary>
    public Action? SchedulerHandler { get; set; }

    /// <summary>How many browser extensions are connected; the app sets it, the status bar reads it on <see cref="Poll"/>.</summary>
    public Func<int>? ConnectedBrowsers { get; set; }

    /// <summary>A running download was opened (double-click or Enter); the details window of M2.9c answers it.</summary>
    public event Action<JobRowViewModel>? DetailsRequested;

    /// <summary>Raised when the search box should take the keyboard (Ctrl+F).</summary>
    public event Action? SearchRequested;

    public string Title => Strings.AppTitle;

    public string AutomationName => Strings.MainWindowAutomationName;

    public string RefreshLinkLabel => Strings.MenuRefreshLink;

    public string CountsText => string.Format(CultureInfo.CurrentCulture, _countsFormat, Jobs.ActiveCount, Jobs.QueuedCount);

    public string ToolbarSpeedLimitText => string.Format(CultureInfo.CurrentCulture, _toolbarLimitFormat, LimitName());

    /// <summary>The grid hands over what is selected; commands act on it.</summary>
    public void SetSelection(IEnumerable<JobRowViewModel> rows)
    {
        _selection = [.. rows];
        SelectedRow = _selection.Count > 0 ? _selection[0] : null;
        RefreshSelectionCommands();
    }

    /// <summary>Refreshes what changes without an event: the connected browsers. The app calls it once a second.</summary>
    public void Poll()
    {
        var count = ConnectedBrowsers?.Invoke() ?? 0;
        ExtensionStatusText = count > 0 ? Strings.StatusExtensionConnected : Strings.StatusExtensionNone;
    }

    public void Dispose() => _limit.Changed -= OnLimitChanged;

    [RelayCommand]
    private void AddUrl() => AddUrlHandler?.Invoke();

    [RelayCommand]
    private void OpenSettings() => SettingsHandler?.Invoke();

    [RelayCommand(CanExecute = nameof(HasResumable))]
    private async Task ResumeAsync()
    {
        foreach (var row in _selection.ToList())
        {
            switch (row.StateValue)
            {
                case JobState.Queued:
                    await TryAsync(() => _service.StartAsync(row.Id)).ConfigureAwait(true);
                    break;
                case JobState.Paused:
                    await TryAsync(() => _service.ResumeAsync(row.Id)).ConfigureAwait(true);
                    break;
            }
        }
    }

    // Stop is Pause until the scheduler (M2.10) has a queue that would start the job again by itself.
    [RelayCommand(CanExecute = nameof(HasPausable))]
    private Task PauseAsync() => PauseSelectedAsync();

    [RelayCommand(CanExecute = nameof(HasPausable))]
    private Task StopAsync() => PauseSelectedAsync();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var rows = _selection.ToList();
        var message = rows.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, _deleteOneFormat, rows[0].FileName)
            : string.Format(CultureInfo.CurrentCulture, _deleteManyFormat, rows.Count);
        await DeleteRowsAsync(rows, message, offerFiles: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteCompletedAsync()
    {
        var rows = Jobs.All.Where(r => r.StateValue == JobState.Completed).ToList();
        if (rows.Count > 0)
        {
            await DeleteRowsAsync(rows, Strings.DeleteCompletedMessage, offerFiles: false).ConfigureAwait(true);
        }
    }

    // The three queue buttons are active (M2.10).
    [RelayCommand]
    private async Task StartQueueAsync()
    {
        int queueId = Sidebar.Selected?.Filter?.QueueId ?? 1;
        await _service.StartQueueAsync(queueId);
    }

    [RelayCommand]
    private async Task StopQueueAsync()
    {
        int queueId = Sidebar.Selected?.Filter?.QueueId ?? 1;
        await _service.StopQueueAsync(queueId);
    }

    [RelayCommand]
    private void Scheduler()
    {
        SchedulerHandler?.Invoke();
    }

    [RelayCommand]
    private void SetSpeedLimit(SpeedPreset? preset)
    {
        if (preset is not null)
        {
            _limit.Set(preset.BytesPerSecond);
        }
    }

    [RelayCommand]
    private void FocusSearch() => SearchRequested?.Invoke();

    /// <summary>Space: pause what is running, else resume what is paused or queued.</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (HasPausable())
        {
            await PauseSelectedAsync().ConfigureAwait(true);
        }
        else if (HasResumable())
        {
            await ResumeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Double-click or Enter: a completed download opens its file, anything else its details.</summary>
    [RelayCommand]
    private async Task OpenRowAsync(JobRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.StateValue == JobState.Completed)
        {
            if (await _service.GetAsync(row.Id).ConfigureAwait(true) is { } job)
            {
                _shell.OpenFile(Path.Combine(job.Folder, job.FileName));
            }
        }
        else
        {
            DetailsRequested?.Invoke(row);
        }
    }

    // only a paused download has a link worth replacing
    [RelayCommand(CanExecute = nameof(CanRefreshLink))]
    private void RefreshLink() => RefreshLinkHandler?.Invoke(SelectedRow!.Id);

    private bool CanRefreshLink() => SelectedRow is { StateValue: JobState.Paused };

    private bool HasSelection() => _selection.Count > 0;

    private bool HasResumable() => _selection.Any(r => r.StateValue is JobState.Paused or JobState.Queued);

    private bool HasPausable() => _selection.Any(r => r.IsActive && r.StateValue != JobState.Muxing && r.StateValue != JobState.Verifying);

    private static bool Never() => false;

    private void RefreshSelectionCommands()
    {
        ResumeCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RefreshLinkCommand.NotifyCanExecuteChanged();
    }

    private async Task PauseSelectedAsync()
    {
        foreach (var row in _selection.Where(r => r.IsActive).ToList())
        {
            await TryAsync(() => _service.PauseAsync(row.Id)).ConfigureAwait(true);
        }
    }

    private async Task DeleteRowsAsync(IReadOnlyList<JobRowViewModel> rows, string message, bool offerFiles)
    {
        var deleteFiles = false;
        if (_ui.Current.ConfirmDelete)
        {
            var choice = await _confirm.ConfirmDeleteAsync(new ConfirmDeleteViewModel(message, offerFiles)).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            deleteFiles = offerFiles && choice.AlsoDeleteFiles; // Delete completed never touches files, whatever the window says
            if (choice.DontAskAgain)
            {
                await _ui.SaveAsync(_ui.Current with { ConfirmDelete = false }).ConfigureAwait(true);
            }
        }

        foreach (var row in rows)
        {
            await TryAsync(() => _service.CancelAsync(row.Id, deleteFiles)).ConfigureAwait(true);
        }
    }

    // a job that changed state meanwhile, or is already gone, must not stop the rest
    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // not in the state we saw
        }
    }

    private void OnLimitChanged(long? bytesPerSecond) => UpdateLimitText();

    private void UpdateLimitText()
    {
        SpeedLimitText = string.Format(CultureInfo.CurrentCulture, _limitFormat, LimitName());
        OnPropertyChanged(nameof(ToolbarSpeedLimitText));
    }

    private string LimitName() => _limit.Current is { } bps ? ByteText.Format(bps) + "/s" : Strings.StatusUnlimited;

    /// <summary>URLs asked for by a launch, waiting for the Add dialog (PR 2.8d).</summary>
    public ObservableCollection<string> PendingAddUrls { get; } = [];

    /// <summary>Downloads the browser handed over, waiting for the Add dialog (PR 2.8d). Holds cookie values: never printed or logged.</summary>
    public ObservableCollection<BrowserDownload> PendingBrowserDownloads { get; } = [];

    /// <summary>Link batches waiting for the link picker (PR 2.8d).</summary>
    public ObservableCollection<BrowserBatch> PendingBrowserBatches { get; } = [];

    public void ShowAdd(BrowserDownload download) => PendingBrowserDownloads.Add(download);

    public void ShowPicker(BrowserBatch batch) => PendingBrowserBatches.Add(batch);

    public void RequestAdd(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            PendingAddUrls.Add(url);
        }
    }
}
