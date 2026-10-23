using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Resources;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>One line of the connection table.</summary>
internal sealed record ConnectionRow(string Connection, string Range, string Speed, string State);

/// <summary>
/// The Download details window (KASI-UI-003, wireframe page 2): what is being saved and where, how far it is, the segment map, one line
/// per connection and the log tail. It follows the engine's <see cref="SegmentsProgressed"/> (about four a second) and applies every one
/// at once, so the map moves at least twice a second. It shows the address without its query (a query can carry a token) and never logs.
/// </summary>
internal sealed partial class DetailsViewModel : ObservableObject, IDisposable
{
    public const int MaxLogLines = 100;

    private static readonly CompositeFormat _savingTo = CompositeFormat.Parse(Strings.DetailsSavingTo);
    private static readonly CompositeFormat _mapSummary = CompositeFormat.Parse(Strings.DetailsMapSummary);

    private readonly IJobService _service;
    private readonly IShell _shell;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private Job? _job;

    [ObservableProperty]
    private IReadOnlyList<Segment> _segments = [];

    [ObservableProperty]
    private string _mapSummaryText = string.Empty;

    [ObservableProperty]
    private string _savingToText = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _resumeSupportText = string.Empty;

    public DetailsViewModel(JobRowViewModel row, IJobService service, IShell shell, Action<Action>? post = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        _service = service;
        _shell = shell;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The list's row for this job, so the numbers here are the numbers there.</summary>
    public JobRowViewModel Row { get; }

    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    /// <summary>Newest first, at most <see cref="MaxLogLines"/>.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Set by the app: asks for a fresh link for this job.</summary>
    public Action<JobId>? RefreshLinkHandler { get; set; }

    public event Action? Closed;

    public string Title => Strings.DetailsTitle;

    public string AutomationName => Strings.DetailsAutomationName;

    public string DownloadedLabel => Strings.DetailsDownloaded;

    public string SpeedLabel => Strings.DetailsSpeed;

    public string TimeLeftLabel => Strings.DetailsTimeLeft;

    public string ResumeLabel => Strings.DetailsResume;

    public string MapLabel => Strings.DetailsSegmentMap;

    public string MapHint => Strings.DetailsMapHint;

    public string ConnectionColumn => Strings.DetailsColConnection;

    public string RangeColumn => Strings.DetailsColRange;

    public string SpeedColumn => Strings.DetailsColSpeed;

    public string StateColumn => Strings.DetailsColState;

    public string LogLabel => Strings.DetailsLog;

    public string RefreshLinkLabel => Strings.DetailsRefreshLink;

    public string OpenFolderLabel => Strings.DetailsOpenFolder;

    public string PauseOrResumeLabel => Row.StateValue == JobState.Paused ? Strings.DetailsResumeButton : Strings.DetailsPause;

    public string CloseLabel => Strings.DetailsClose;

    /// <summary>Reads the job once and starts following the engine. Call on the UI thread.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _service.EventRaised += OnEvent;
        Row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobRowViewModel.StateValue))
            {
                OnPropertyChanged(nameof(PauseOrResumeLabel));
                RefreshLinkCommand.NotifyCanExecuteChanged();
                PauseOrResumeCommand.NotifyCanExecuteChanged();
            }
        };
        _job = await _service.GetAsync(Row.Id, cancellationToken).ConfigureAwait(true);
        if (_job is { } job)
        {
            Address = WithoutQuery(job.FinalUrl ?? job.Url);
            SavingToText = string.Format(CultureInfo.CurrentCulture, _savingTo, job.Folder);
            ResumeSupportText = job.Resumable ? Strings.DetailsResumeSupported : Strings.DetailsResumeNotSupported;
        }

        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _service.EventRaised -= OnEvent;

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenFolder() => _shell.ShowInFolder(Path.Combine(_job!.Folder, _job.FileName));

    private bool HasFolder() => _job is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshLink))]
    private void RefreshLink() => RefreshLinkHandler?.Invoke(Row.Id);

    private bool CanRefreshLink() => Row.StateValue == JobState.Paused;

    [RelayCommand(CanExecute = nameof(CanPauseOrResume))]
    private async Task PauseOrResumeAsync()
    {
        try
        {
            if (Row.StateValue == JobState.Paused)
            {
                await _service.ResumeAsync(Row.Id).ConfigureAwait(true);
            }
            else if (Row.StateValue == JobState.Queued)
            {
                await _service.StartAsync(Row.Id).ConfigureAwait(true);
            }
            else
            {
                await _service.PauseAsync(Row.Id).ConfigureAwait(true);
            }
        }
        catch (InvalidOperationException)
        {
            // the job changed state meanwhile
        }
    }

    private bool CanPauseOrResume() => Row.StateValue is JobState.Paused or JobState.Queued || Row.IsActive;

    private void OnEvent(EngineEvent engineEvent)
    {
        switch (engineEvent)
        {
            case SegmentsProgressed shape when shape.Id == Row.Id:
                _post(() => Apply(shape));
                break;
            case JobLogged logged when logged.Id == Row.Id:
                _post(() => AddLog(logged.At, logged.Line));
                break;
            case JobStateChanged changed when changed.Id == Row.Id:
                _post(() => AddLog(_time.GetUtcNow(), JobText.State(changed.To)));
                break;
        }
    }

    private void Apply(SegmentsProgressed shape)
    {
        Segments = shape.Segments;
        var done = shape.Segments.Sum(s => s.Written);
        var total = shape.Segments.Sum(s => s.Length);
        MapSummaryText = string.Format(CultureInfo.CurrentCulture, _mapSummary, shape.Connections.Count, total > 0 ? (int)(done * 100 / total) : 0);
        Connections.Clear();
        foreach (var connection in shape.Connections)
        {
            Connections.Add(new ConnectionRow(
                (connection.Worker + 1).ToString(CultureInfo.CurrentCulture),
                ByteText.Format(connection.Start) + " - " + ByteText.Format(connection.End),
                JobText.Speed(connection.BytesPerSecond),
                Strings.DetailsReceiving));
        }
    }

    private void AddLog(DateTimeOffset at, string line)
    {
        Log.Insert(0, at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) + " " + line);
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    /// <summary>The address as far as the path: a query can carry a token, and the window may be screenshotted.</summary>
    internal static string WithoutQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }
}
