using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.Core;

namespace Kasi.App.ViewModels;

internal enum RefreshPhase
{
    Ask,
    Waiting,
    Done,
    Failed,
}

/// <summary>
/// The refresh-link dialog: says which download lost its link, offers to open the page it came from in the browser, and reports how it
/// went. Shows the file name only, never the address. Cancelling while waiting stops the wait (the browser's tab closes itself).
/// </summary>
internal sealed partial class RefreshLinkViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat _askFormat = CompositeFormat.Parse(Strings.RefreshAsk);

    private readonly ILinkRefresher _refresher;
    private readonly JobId _id;
    private CancellationTokenSource? _waiting;

    [ObservableProperty]
    private RefreshPhase _phase = RefreshPhase.Ask;

    [ObservableProperty]
    private string _statusText;

    public RefreshLinkViewModel(JobId id, string fileName, ILinkRefresher refresher)
    {
        _id = id;
        _refresher = refresher;
        _statusText = string.Format(CultureInfo.CurrentCulture, _askFormat, fileName);
        FileName = fileName;
    }

    public string FileName { get; }

    /// <summary>How the last try ended; null before one has.</summary>
    public RefreshOutcome? Outcome { get; private set; }

    public event Action? Closed;

    public string Title => Strings.RefreshTitle;

    public string AutomationName => Strings.RefreshAutomationName;

    public string StartLabel => Strings.RefreshStart;

    public string CancelLabel => Strings.AddCancelButton;

    public string CloseLabel => Strings.RefreshClose;

    public bool CanStart => Phase is RefreshPhase.Ask or RefreshPhase.Failed;

    public bool IsWaiting => Phase == RefreshPhase.Waiting;

    public bool IsFinished => Phase == RefreshPhase.Done;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        using var waiting = new CancellationTokenSource();
        _waiting = waiting;
        Phase = RefreshPhase.Waiting;
        StatusText = Strings.RefreshWaiting;
        RefreshOutcome outcome;
        try
        {
            outcome = await _refresher.RefreshAsync(_id, waiting.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // cancelled: the window is closing
        }
        finally
        {
            _waiting = null;
        }

        Outcome = outcome;
        Phase = outcome == RefreshOutcome.Refreshed ? RefreshPhase.Done : RefreshPhase.Failed;
        StatusText = outcome switch
        {
            RefreshOutcome.Refreshed => Strings.RefreshDone,
            RefreshOutcome.NoReferrer => Strings.RefreshNoReferrer,
            RefreshOutcome.NoBrowser => Strings.RefreshNoBrowser,
            RefreshOutcome.TimedOut => Strings.RefreshTimedOut,
            RefreshOutcome.Rejected => Strings.RefreshRejected,
            _ => Strings.RefreshFailed,
        };
    }

    [RelayCommand]
    private void Cancel()
    {
        _waiting?.Cancel();
        Closed?.Invoke();
    }

    public void Dispose() => _waiting?.Cancel();

    partial void OnPhaseChanged(RefreshPhase value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsFinished));
        StartCommand.NotifyCanExecuteChanged();
    }
}
