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

/// <summary>One tile of the segment matrix: a connection with the share of its segment received and its speed, or "Idle" when it has nothing to do.</summary>
internal sealed record MatrixTile(string Number, int Percent, string PercentText, string SpeedText, bool IsIdle);

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
    private static readonly CompositeFormat _streamsActive = CompositeFormat.Parse(Strings.DetailsStreamsActive);
    private static readonly CompositeFormat _activeStreams = CompositeFormat.Parse(Strings.DetailsActiveStreams);
    private static readonly CompositeFormat _matrix = CompositeFormat.Parse(Strings.DetailsMatrix);

    private readonly IJobService _service;
    private readonly IShell _shell;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly SpeedHistory _history = new();
    private readonly Dictionary<int, MatrixTile> _tiles = [];
    private Job? _job;
    private DateTimeOffset _lastSample;
    private DateTimeOffset? _startedAt;
    private TimeSpan _elapsed;
    private double _peak;

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

    [ObservableProperty]
    private IReadOnlyList<double> _speedSamples = [];

    [ObservableProperty]
    private string _peakText = string.Empty;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _streamsText = string.Empty;

    [ObservableProperty]
    private string _protocolText = string.Empty;

    [ObservableProperty]
    private string _matrixTitle = string.Empty;

    [ObservableProperty]
    private string _activeStreamsText = string.Empty;

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

    /// <summary>One tile per connection the job has used, in connection order.</summary>
    public ObservableCollection<MatrixTile> Tiles { get; } = [];

    /// <summary>"64.2%": the headline number.</summary>
    public string PercentText => Row.Percent.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    public string StatusLine => string.IsNullOrEmpty(StreamsText) ? Row.State : Row.State + " (" + StreamsText + ")";

    public string PeakLabel => Strings.DetailsPeak;

    public string ElapsedLabel => Strings.DetailsElapsed;

    public string ChartLabel => Strings.DetailsChart;

    public string CopyLinkLabel => Strings.DetailsCopyLink;

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
            if (e.PropertyName == nameof(JobRowViewModel.Percent))
            {
                OnPropertyChanged(nameof(PercentText));
            }

            if (e.PropertyName == nameof(JobRowViewModel.StateValue))
            {
                TrackElapsed();
                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(PauseOrResumeLabel));
                RefreshLinkCommand.NotifyCanExecuteChanged();
                PauseOrResumeCommand.NotifyCanExecuteChanged();
            }
        };
        TrackElapsed();
        _job = await _service.GetAsync(Row.Id, cancellationToken).ConfigureAwait(true);
        if (_job is { } job)
        {
            Address = WithoutQuery(job.FinalUrl ?? job.Url);
            SavingToText = string.Format(CultureInfo.CurrentCulture, _savingTo, job.Folder);
            ProtocolText = (Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) ? uri.Scheme : string.Empty).ToUpperInvariant();
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

    // the clock runs while the job transfers and stands still otherwise: it counts this run, not the sum of the earlier ones
    private void TrackElapsed()
    {
        var now = _time.GetUtcNow();
        if (Row.IsActive)
        {
            _startedAt ??= now;
        }
        else if (_startedAt is { } started)
        {
            _elapsed += now - started;
            _startedAt = null;
        }

        ElapsedText = ElapsedNow(now);
    }

    private string ElapsedNow(DateTimeOffset now)
    {
        var total = _elapsed + (_startedAt is { } started ? now - started : TimeSpan.Zero);
        return total.TotalHours >= 1
            ? total.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : total.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void Apply(SegmentsProgressed shape)
    {
        var now = _time.GetUtcNow();
        ElapsedText = ElapsedNow(now);
        if (now - _lastSample >= TimeSpan.FromSeconds(1))
        {
            _lastSample = now;
            var speed = shape.Connections.Sum(c => c.BytesPerSecond);
            _history.Add(speed);
            _peak = Math.Max(_peak, speed);
            SpeedSamples = _history.Snapshot();
            PeakText = JobText.Speed((long)_peak);
        }

        ApplyTiles(shape);
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

    private void ApplyTiles(SegmentsProgressed shape)
    {
        var live = shape.Connections.ToDictionary(c => c.Worker);
        foreach (var (worker, connection) in live)
        {
            var segment = connection.SegmentIndex >= 0 && connection.SegmentIndex < shape.Segments.Count ? shape.Segments[connection.SegmentIndex] : null;
            var percent = segment is { Length: > 0 } ? (int)(segment.Written * 100 / segment.Length) : 0;
            _tiles[worker] = new MatrixTile(Number(worker), percent, percent.ToString(CultureInfo.CurrentCulture) + "%", JobText.Speed(connection.BytesPerSecond), false);
        }

        foreach (var worker in _tiles.Keys.Where(w => !live.ContainsKey(w)).ToList())
        {
            var last = _tiles[worker];
            _tiles[worker] = last with { SpeedText = Strings.DetailsIdle, IsIdle = true };
        }

        var ordered = _tiles.OrderBy(t => t.Key).Select(t => t.Value).ToList();
        Tiles.Clear();
        foreach (var tile in ordered)
        {
            Tiles.Add(tile);
        }

        StreamsText = string.Format(CultureInfo.CurrentCulture, _streamsActive, live.Count);
        ActiveStreamsText = string.Format(CultureInfo.CurrentCulture, _activeStreams, live.Count, _tiles.Count);
        MatrixTitle = string.Format(CultureInfo.CurrentCulture, _matrix, _tiles.Count);
        OnPropertyChanged(nameof(StatusLine));
    }

    private static string Number(int worker) => "#" + (worker + 1).ToString("00", CultureInfo.CurrentCulture);

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
